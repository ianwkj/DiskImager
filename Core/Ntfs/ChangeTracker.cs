using System.Text;
using System.Text.Json;

namespace DiskImager.Core.Ntfs;

/// <summary>What one NTFS volume looked like at a backup: who owned each block, and where its change journal was.</summary>
public sealed class VolumeTrackingState
{
    public int Version { get; set; } = 1;
    /// <summary>The image this state was recorded for (an incremental may only use the state of its own parent).</summary>
    public Guid ImageId { get; set; }
    public long PartitionOffset { get; set; }
    public int BlockSize { get; set; }
    public int ClusterSize { get; set; }
    public ulong JournalId { get; set; }
    /// <summary>Next change-journal position at the snapshot: everything from here on is new.</summary>
    public long NextUsn { get; set; }
    /// <summary>Per block of the partition (index relative to the partition start), a fingerprint of which files' data sits there.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong[] Owners { get; set; } = Array.Empty<ulong>();

    /// <summary><see cref="Owners"/> as bytes (stored as base64: much smaller than a list of numbers).</summary>
    public byte[] OwnersData
    {
        get => System.Runtime.InteropServices.MemoryMarshal.AsBytes(Owners.AsSpan()).ToArray();
        set => Owners = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(value).ToArray();
    }
}

/// <summary>
/// Change tracking without a driver, for faster incrementals on NTFS. A block can be skipped (taken from the
/// previous image without reading the disk) only if all of these hold since the previous backup:
///   • the same files own the same parts of it (catches files moved by defrag, deleted, grown or shrunk);
///   • no file owning it appears in the volume's change journal (catches data rewritten in place);
///   • it doesn't belong to NTFS's own metadata, a folder index that changed, or anything unaccounted for.
/// Anything uncertain (journal reset or overflowed, no state for the parent image) means: read everything.
/// A small random sample of skipped blocks is read anyway and compared, as a safety check.
/// </summary>
public static class ChangeTracker
{
    private const ulong UnownedMarker = 0x9E3779B97F4A7C15UL;

    public sealed class Scan
    {
        public ulong[] Owners = Array.Empty<ulong>();
        /// <summary>Blocks (partition-relative) belonging to each MFT record (base record number), for journal lookups.</summary>
        public Dictionary<long, List<(long First, long Last)>> RecordBlocks = new();
        /// <summary>Blocks of NTFS metadata ($MFT, $LogFile, $Bitmap, $Extend\..., …): always read.</summary>
        public HashSet<long> MetadataBlocks = new();
        public int ClusterSize;
        public ulong JournalId;
        public long NextUsn = -1;
        public long LowestValidUsn;
        public Stream? Journal;
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
        return x;
    }

    private static ulong NameHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Reads the whole MFT once and builds the block ownership map, plus the change journal position.</summary>
    public static Scan ScanVolume(NtfsVolume vol, int blockSize, long partitionLength)
    {
        long blocks = (partitionLength + blockSize - 1) / blockSize;
        var scan = new Scan { Owners = new ulong[blocks], ClusterSize = vol.ClusterSize };
        int cs = vol.ClusterSize;

        void Own(long baseRecord, int type, string name, NtfsAttribute a, bool metadata)
        {
            ulong id = Mix((ulong)baseRecord * 0x100000001B3UL ^ (ulong)type << 48 ^ NameHash(name));
            foreach (var run in a.Runs)
            {
                if (run.Lcn < 0 || run.Length <= 0) continue;
                long start = run.Lcn * cs, end = (run.Lcn + run.Length) * cs;
                long first = start / blockSize, last = (end - 1) / blockSize;
                if (first >= blocks) continue;
                last = Math.Min(last, blocks - 1);
                for (long b = first; b <= last; b++)
                {
                    long s = Math.Max(start, b * blockSize), e = Math.Min(end, (b + 1) * blockSize);
                    long vcnAtStart = run.Vcn + (s - start) / cs;
                    scan.Owners[b] ^= Mix(id ^ (ulong)vcnAtStart * 0x9E3779B97F4A7C15UL ^ (ulong)(e - s) << 20);
                    if (metadata) scan.MetadataBlocks.Add(b);
                }
                if (!scan.RecordBlocks.TryGetValue(baseRecord, out var list)) scan.RecordBlocks[baseRecord] = list = new();
                list.Add((first, last));
            }
        }

        // Records of $Extend's children ($UsnJrnl, $ObjId, $Quota, $Reparse, …) are metadata too.
        var extendChildren = new HashSet<long>();
        try { foreach (var e in vol.ListDirectory(11, "\\$Extend")) extendChildren.Add(e.Record); } catch { }

        foreach (var (number, rec) in vol.EnumerateRecords())
        {
            long owner = rec.BaseRecord != 0 ? rec.BaseRecord : number;
            bool metadata = owner < 27 || extendChildren.Contains(owner);
            foreach (var a in rec.Attributes.Where(a => a.NonResident))
                Own(owner, a.Type, a.Name, a, metadata);
        }
        // A block no file claims at all still gets a fixed marker, so "now unowned" differs from "owned".
        for (long b = 0; b < blocks; b++) if (scan.Owners[b] == 0) scan.Owners[b] = UnownedMarker;

        ReadJournalPosition(vol, extendChildren, scan);
        return scan;
    }

    private static void ReadJournalPosition(NtfsVolume vol, HashSet<long> extendChildren, Scan scan)
    {
        foreach (var rec in extendChildren.Select(vol.GetRecord).Where(r => r != null))
        {
            var max = rec!.Find(NtfsAttribute.Data, "$Max");
            var j = rec.Find(NtfsAttribute.Data, "$J");
            if (max == null || j == null) continue;
            var m = vol.ReadAll(max);
            if (m.Length < 32) continue;
            scan.JournalId = BitConverter.ToUInt64(m, 16);
            scan.LowestValidUsn = BitConverter.ToInt64(m, 24);
            scan.NextUsn = j.RealSize;
            scan.Journal = vol.OpenAttribute(j);
            return;
        }
    }

    /// <summary>Records (file numbers) mentioned in the change journal from <paramref name="fromUsn"/> on, with their parent folders.</summary>
    public static HashSet<long> ChangedRecords(Stream journal, long fromUsn, long toUsn)
    {
        var changed = new HashSet<long>();
        var buf = new byte[1 << 16];
        long pos = fromUsn;
        while (pos < toUsn)
        {
            journal.Position = pos;
            int n = (int)Math.Min(buf.Length, toUsn - pos);
            journal.ReadExactly(buf, 0, n);
            int p = 0;
            while (p + 8 <= n)
            {
                int len = BitConverter.ToInt32(buf, p);
                if (len == 0) { p = (int)(((pos + p) / 4096 + 1) * 4096 - pos); continue; } // padding to the end of a 4 KB page
                if (len < 56 || p + len > n) break;
                int major = BitConverter.ToUInt16(buf, p + 4);
                if (major == 2)
                {
                    changed.Add(BitConverter.ToInt64(buf, p + 8) & 0xFFFFFFFFFFFF);
                    changed.Add(BitConverter.ToInt64(buf, p + 16) & 0xFFFFFFFFFFFF);
                }
                else if (major == 3)
                {
                    changed.Add(BitConverter.ToInt64(buf, p + 8) & 0xFFFFFFFFFFFF);  // low 48 bits of the 128-bit ID
                    changed.Add(BitConverter.ToInt64(buf, p + 24) & 0xFFFFFFFFFFFF);
                }
                p += (len + 7) & ~7;
            }
            if (p == 0) p = Math.Min(n, 4096); // nothing parsable here: move on a page
            pos += p;
        }
        return changed;
    }

    /// <summary>
    /// Blocks (partition-relative) that may have changed since <paramref name="previous"/>, or null if that can't be
    /// worked out safely (then the whole volume must be read).
    /// </summary>
    public static HashSet<long>? ChangedBlocks(Scan now, VolumeTrackingState previous, int blockSize, int clusterSize, out string why)
    {
        why = "";
        if (previous.BlockSize != blockSize || previous.ClusterSize != clusterSize || previous.Owners.Length != now.Owners.Length)
        { why = "the volume's layout changed"; return null; }
        if (now.Journal == null || now.NextUsn < 0) { why = "the volume has no change journal"; return null; }
        if (now.JournalId != previous.JournalId) { why = "the change journal was reset"; return null; }
        if (previous.NextUsn < now.LowestValidUsn) { why = "the change journal overflowed since the last backup"; return null; }
        if (previous.NextUsn > now.NextUsn) { why = "the change journal went backwards"; return null; }

        var changed = new HashSet<long>(now.MetadataBlocks);
        for (long b = 0; b < now.Owners.Length; b++)
            if (now.Owners[b] != previous.Owners[b] || now.Owners[b] == UnownedMarker) changed.Add(b);
        foreach (var record in ChangedRecords(now.Journal, previous.NextUsn, now.NextUsn))
            if (now.RecordBlocks.TryGetValue(record, out var ranges))
                foreach (var (first, last) in ranges)
                    for (long b = first; b <= last; b++) changed.Add(b);
        return changed;
    }

    // ------------------------------------------------------------------ saved state

    public static string StateFolder => Path.Combine(JobStore.DataFolder, "Tracking");

    public static string StatePath(Guid jobId, long partitionOffset) => Path.Combine(StateFolder, $"{jobId:N}-{partitionOffset}.json");

    public static VolumeTrackingState? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<VolumeTrackingState>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    public static void Save(string path, VolumeTrackingState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, path, overwrite: true);
    }
}
