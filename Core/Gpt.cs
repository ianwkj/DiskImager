namespace DiskImager.Core;

/// <summary>
/// Adjusts a copied partition table for a target disk of a different size: rewrites the GPT
/// header's backup location and CRCs, and builds the backup GPT that lives at the end of the disk.
/// </summary>
public static class Gpt
{
    public sealed class Fixup
    {
        public byte[] Backup = Array.Empty<byte>();
        public long BackupOffset;
    }

    public static Fixup ForTarget(byte[] block0, int block0Length, int ss, long targetSize, bool newIds)
    {
        int h = ss; // primary header at LBA 1
        if (block0Length < 2 * ss || BitConverter.ToUInt64(block0, h) != 0x5452415020494645UL) // "EFI PART"
            throw new InvalidDataException("The image's GPT header is missing or damaged.");

        int headerSize = BitConverter.ToInt32(block0, h + 12);
        long entriesLba = BitConverter.ToInt64(block0, h + 72);
        int numEntries = BitConverter.ToInt32(block0, h + 80);
        int entrySize = BitConverter.ToInt32(block0, h + 84);
        int entriesBytes = numEntries * entrySize;
        long entriesOff = entriesLba * ss;
        if (headerSize < 92 || headerSize > ss || entriesOff + entriesBytes > block0Length)
            throw new InvalidDataException("Unsupported GPT layout in image.");

        long lastLba = targetSize / ss - 1;
        int entriesSectors = (entriesBytes + ss - 1) / ss;
        long backupEntriesLba = lastLba - entriesSectors;
        long lastUsable = backupEntriesLba - 1;

        var entries = block0.AsSpan((int)entriesOff, entriesBytes);
        for (int i = 0; i < numEntries; i++)
        {
            var e = entries.Slice(i * entrySize, entrySize);
            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
            long last = BitConverter.ToInt64(e[40..]);
            if (last > lastUsable)
                throw new InvalidOperationException(
                    $"The target disk is too small: it needs at least {Util.FormatBytes((last + 1 + entriesSectors + 1) * (double)ss)}.");
            if (newIds) Guid.NewGuid().ToByteArray().CopyTo(e[16..]);
        }
        if (newIds) Guid.NewGuid().ToByteArray().CopyTo(block0, h + 56);

        uint entriesCrc = Crc32.Compute(entries);
        BitConverter.TryWriteBytes(block0.AsSpan(h + 32), lastLba);
        BitConverter.TryWriteBytes(block0.AsSpan(h + 48), lastUsable);
        BitConverter.TryWriteBytes(block0.AsSpan(h + 88), entriesCrc);
        SetHeaderCrc(block0.AsSpan(h, headerSize));

        // Protective MBR: partition 0xEE should span the whole disk (capped at 2 TiB).
        for (int i = 0; i < 4; i++)
        {
            int pe = 446 + i * 16;
            if (block0[pe + 4] == 0xEE)
                BitConverter.TryWriteBytes(block0.AsSpan(pe + 12), (uint)Math.Min(lastLba, uint.MaxValue));
        }

        // Backup: entries, then header in the very last sector.
        var backup = new byte[(entriesSectors + 1) * ss];
        entries.CopyTo(backup);
        var bh = backup.AsSpan(entriesSectors * ss, headerSize);
        block0.AsSpan(h, headerSize).CopyTo(bh);
        BitConverter.TryWriteBytes(bh[24..], lastLba);          // MyLBA
        BitConverter.TryWriteBytes(bh[32..], 1L);               // AlternateLBA
        BitConverter.TryWriteBytes(bh[72..], backupEntriesLba); // PartitionEntryLBA
        SetHeaderCrc(bh);

        return new Fixup { Backup = backup, BackupOffset = backupEntriesLba * ss };
    }

    private static void SetHeaderCrc(Span<byte> header)
    {
        BitConverter.TryWriteBytes(header[16..], 0u);
        BitConverter.TryWriteBytes(header[16..], Crc32.Compute(header));
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
