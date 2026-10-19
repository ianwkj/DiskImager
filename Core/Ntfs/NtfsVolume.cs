using System.Text;

namespace DiskImager.Core.Ntfs;

/// <summary>A run of clusters: Lcn = -1 for a sparse (unallocated) run.</summary>
public readonly record struct DataRun(long Vcn, long Lcn, long Length);

/// <summary>One attribute of a file (merged across MFT records when it was split).</summary>
public sealed class NtfsAttribute
{
    public const int StandardInformation = 0x10, AttributeList = 0x20, FileName = 0x30, Data = 0x80,
        IndexRoot = 0x90, IndexAllocation = 0xA0, Bitmap = 0xB0, ReparsePoint = 0xC0;
    public const ushort FlagCompressed = 0x0001, FlagEncrypted = 0x4000, FlagSparse = 0x8000;

    public int Type { get; init; }
    public string Name { get; init; } = "";
    public bool NonResident { get; init; }
    public ushort Flags { get; init; }
    public byte[]? Value { get; set; }
    public List<DataRun> Runs { get; } = new();
    public long RealSize { get; set; }
    public long InitializedSize { get; set; }
    public int CompressionUnit { get; set; }
    public bool IsCompressed => (Flags & FlagCompressed) != 0;
    public bool IsEncrypted => (Flags & FlagEncrypted) != 0;
}

/// <summary>A file or folder as listed in a directory.</summary>
public sealed class NtfsEntry
{
    public long Record { get; init; }
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public FileAttributes Attributes { get; set; }
    /// <summary>Path from the volume root, e.g. \Users\amy\Documents\a.docx.</summary>
    public string Path { get; set; } = "";
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
    /// <summary>Kind of reparse point (0 if none).</summary>
    public uint ReparseTag { get; set; }
    /// <summary>A junction or symbolic link: points somewhere else, has no data of its own.</summary>
    public bool IsLink => IsReparsePoint && ReparseTag is NtfsVolume.TagMountPoint or NtfsVolume.TagSymlink;
}

/// <summary>A parsed MFT record (with its extension records' attributes folded in).</summary>
public sealed class NtfsRecord
{
    public long Number { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }
    public long BaseRecord { get; init; }
    public List<NtfsAttribute> Attributes { get; } = new();

    public NtfsAttribute? Find(int type, string name = "") =>
        Attributes.FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Read-only NTFS reader: enough to list folders and copy files out of a backup image without Windows mounting
/// it (no driver needed, so it also works in the rescue environment). Handles resident, fragmented, sparse and
/// NTFS-compressed files, attribute lists, large folders and Windows' per-file (WOF) compression.
/// </summary>
public sealed class NtfsVolume
{
    private readonly IVolumeReader _r;
    private readonly List<DataRun> _mftRuns = new();

    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public int RecordSize { get; }
    public int IndexRecordSize { get; }
    public long TotalClusters { get; }
    public long MftRecordCount { get; private set; }
    public IVolumeReader Reader => _r;

    public const long RootRecord = 5;
    public const uint TagMountPoint = 0xA0000003, TagSymlink = 0xA000000C, TagWof = 0x80000017, TagDedup = 0x80000013;

    public static bool IsNtfs(IVolumeReader r)
    {
        var boot = new byte[512];
        r.Read(0, boot, 0, 512);
        return Encoding.ASCII.GetString(boot, 3, 8) == "NTFS    ";
    }

    public NtfsVolume(IVolumeReader reader)
    {
        _r = reader;
        var boot = new byte[512];
        _r.Read(0, boot, 0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") throw new InvalidDataException("This partition isn't NTFS.");
        BytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        int spc = boot[0x0D];
        int sectorsPerCluster = spc <= 0x80 ? spc : 1 << (256 - spc);
        ClusterSize = BytesPerSector * sectorsPerCluster;
        TotalClusters = BitConverter.ToInt64(boot, 0x28) / sectorsPerCluster;
        long mftLcn = BitConverter.ToInt64(boot, 0x30);
        RecordSize = SizeFromBoot((sbyte)boot[0x40]);
        IndexRecordSize = SizeFromBoot((sbyte)boot[0x44]);
        if (BytesPerSector < 256 || ClusterSize <= 0 || RecordSize < 256 || RecordSize > 65536)
            throw new InvalidDataException("The NTFS boot sector is damaged.");

        // $MFT describes itself: read its record 0 straight from the boot sector's location, then follow its data runs.
        var raw0 = ReadRecordAt(mftLcn * ClusterSize, 0) ?? throw new InvalidDataException("The $MFT record is damaged.");
        var rec0 = Parse(raw0, 0)!;
        var firstPiece = rec0.Attributes.FirstOrDefault(a => a.Type == NtfsAttribute.Data && a.Name == "")
                         ?? throw new InvalidDataException("$MFT has no data.");
        _mftRuns.AddRange(firstPiece.Runs); // enough to reach $MFT's own extension records, if it has any
        var full = LoadRecord(rec0);
        var data = full.Find(NtfsAttribute.Data) ?? throw new InvalidDataException("$MFT has no data.");
        _mftRuns.Clear();
        _mftRuns.AddRange(data.Runs);
        _mftData = data;
        MftRecordCount = data.RealSize / RecordSize;
    }

    private readonly NtfsAttribute _mftData = null!;

    /// <summary>Every in-use MFT record (base and extension records), read sequentially in large chunks.</summary>
    public IEnumerable<(long Number, NtfsRecord Record)> EnumerateRecords()
    {
        int perChunk = Math.Max(1, (1 << 20) / RecordSize);
        var buf = new byte[perChunk * RecordSize];
        using var mft = OpenAttribute(_mftData);
        for (long first = 0; first < MftRecordCount; first += perChunk)
        {
            int count = (int)Math.Min(perChunk, MftRecordCount - first);
            mft.Position = first * RecordSize;
            mft.ReadExactly(buf, 0, count * RecordSize);
            for (int i = 0; i < count; i++)
            {
                if (buf[i * RecordSize] != (byte)'F') continue;
                var raw = buf.AsSpan(i * RecordSize, RecordSize).ToArray();
                if (Encoding.ASCII.GetString(raw, 0, 4) != "FILE" || !ApplyFixups(raw)) continue;
                var rec = Parse(raw, first + i);
                if (rec != null && rec.InUse) yield return (first + i, rec);
            }
        }
    }

    private int SizeFromBoot(sbyte v) => v < 0 ? 1 << -v : v * ClusterSize;

    // ------------------------------------------------------------------ records

    /// <summary>Reads and parses an MFT record (null if it's not in use or damaged).</summary>
    public NtfsRecord? GetRecord(long number)
    {
        if (number < 0 || (MftRecordCount > 0 && number >= MftRecordCount)) return null;
        var raw = ReadRecordRaw(number);
        if (raw == null) return null;
        var rec = Parse(raw, number);
        if (rec == null || !rec.InUse) return null;
        return LoadRecord(rec);
    }

    private byte[]? ReadRecordRaw(long number)
    {
        long vOffset = number * RecordSize;
        long? abs = MapOffset(_mftRuns, vOffset);
        if (abs == null) return null;
        return ReadRecordAt(abs.Value, number);
    }

    private byte[]? ReadRecordAt(long volumeOffset, long number)
    {
        var buf = new byte[RecordSize];
        // A record can straddle two runs when clusters are smaller than records: read it in cluster pieces.
        if (ClusterSize >= RecordSize || _mftRuns.Count == 0) _r.Read(volumeOffset, buf, 0, RecordSize);
        else
        {
            for (int done = 0; done < RecordSize; done += ClusterSize)
            {
                long? a = MapOffset(_mftRuns, number * RecordSize + done);
                if (a == null) return null;
                _r.Read(a.Value, buf, done, Math.Min(ClusterSize, RecordSize - done));
            }
        }
        if (Encoding.ASCII.GetString(buf, 0, 4) != "FILE") return null;
        return ApplyFixups(buf) ? buf : null;
    }

    private long? MapOffset(List<DataRun> runs, long vOffset)
    {
        long vcn = vOffset / ClusterSize;
        foreach (var run in runs)
            if (vcn >= run.Vcn && vcn < run.Vcn + run.Length)
                return run.Lcn < 0 ? null : (run.Lcn + (vcn - run.Vcn)) * ClusterSize + vOffset % ClusterSize;
        return null;
    }

    /// <summary>Undoes NTFS's "update sequence" protection: the last 2 bytes of every 512 are stored in a table.</summary>
    private static bool ApplyFixups(byte[] buf)
    {
        int usaOffset = BitConverter.ToUInt16(buf, 4), usaCount = BitConverter.ToUInt16(buf, 6);
        if (usaOffset + usaCount * 2 > buf.Length || usaCount == 0) return false;
        ushort usn = BitConverter.ToUInt16(buf, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            if (end + 2 > buf.Length) break;
            if (BitConverter.ToUInt16(buf, end) != usn) return false; // torn write
            buf[end] = buf[usaOffset + i * 2];
            buf[end + 1] = buf[usaOffset + i * 2 + 1];
        }
        return true;
    }

    private NtfsRecord? Parse(byte[] buf, long number)
    {
        ushort flags = BitConverter.ToUInt16(buf, 0x16);
        var rec = new NtfsRecord
        {
            Number = number,
            InUse = (flags & 1) != 0,
            IsDirectory = (flags & 2) != 0,
            BaseRecord = BitConverter.ToInt64(buf, 0x20) & 0xFFFFFFFFFFFF,
        };
        int pos = BitConverter.ToUInt16(buf, 0x14);
        int used = Math.Min(BitConverter.ToInt32(buf, 0x18), buf.Length);
        while (pos + 16 <= used)
        {
            int type = BitConverter.ToInt32(buf, pos);
            if (type == -1) break;
            int len = BitConverter.ToInt32(buf, pos + 4);
            if (len < 16 || pos + len > used) break;
            rec.Attributes.Add(ParseAttribute(buf, pos));
            pos += len;
        }
        return rec;
    }

    private static NtfsAttribute ParseAttribute(byte[] buf, int pos)
    {
        int type = BitConverter.ToInt32(buf, pos);
        bool nonResident = buf[pos + 8] != 0;
        int nameLen = buf[pos + 9], nameOff = BitConverter.ToUInt16(buf, pos + 10);
        string name = nameLen > 0 ? Encoding.Unicode.GetString(buf, pos + nameOff, nameLen * 2) : "";
        ushort flags = BitConverter.ToUInt16(buf, pos + 12);
        var a = new NtfsAttribute { Type = type, Name = name, NonResident = nonResident, Flags = flags };
        if (!nonResident)
        {
            int vLen = BitConverter.ToInt32(buf, pos + 0x10), vOff = BitConverter.ToUInt16(buf, pos + 0x14);
            a.Value = buf.AsSpan(pos + vOff, Math.Max(0, Math.Min(vLen, buf.Length - pos - vOff))).ToArray();
            a.RealSize = a.InitializedSize = a.Value.Length;
            return a;
        }
        long startVcn = BitConverter.ToInt64(buf, pos + 0x10);
        int runOff = BitConverter.ToUInt16(buf, pos + 0x20);
        a.CompressionUnit = BitConverter.ToUInt16(buf, pos + 0x22);
        if (startVcn == 0)
        {
            a.RealSize = BitConverter.ToInt64(buf, pos + 0x30);
            a.InitializedSize = BitConverter.ToInt64(buf, pos + 0x38);
        }
        DecodeRuns(buf, pos + runOff, pos + BitConverter.ToInt32(buf, pos + 4), startVcn, a.Runs);
        return a;
    }

    /// <summary>Data-run list: per run a header byte (size of length / offset fields), a length and a signed LCN delta.</summary>
    public static void DecodeRuns(byte[] buf, int pos, int end, long vcn, List<DataRun> runs)
    {
        long lcn = 0;
        while (pos < end && buf[pos] != 0)
        {
            int lenBytes = buf[pos] & 0x0F, offBytes = buf[pos] >> 4;
            pos++;
            if (lenBytes == 0 || pos + lenBytes + offBytes > end) break;
            long length = 0;
            for (int i = 0; i < lenBytes; i++) length |= (long)buf[pos + i] << (8 * i);
            pos += lenBytes;
            if (offBytes == 0)
            {
                runs.Add(new DataRun(vcn, -1, length)); // sparse
            }
            else
            {
                long delta = 0;
                for (int i = 0; i < offBytes; i++) delta |= (long)buf[pos + i] << (8 * i);
                if ((buf[pos + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes); // sign-extend
                lcn += delta;
                runs.Add(new DataRun(vcn, lcn, length));
            }
            pos += offBytes;
            vcn += length;
        }
    }

    /// <summary>Folds in attributes stored in extension records ($ATTRIBUTE_LIST) and merges split attributes.</summary>
    private NtfsRecord LoadRecord(NtfsRecord rec)
    {
        var list = rec.Find(NtfsAttribute.AttributeList);
        if (list != null)
        {
            byte[] value = list.NonResident ? ReadAll(list) : list.Value ?? Array.Empty<byte>();
            var extensions = new HashSet<long>();
            for (int p = 0; p + 26 <= value.Length;)
            {
                int len = BitConverter.ToUInt16(value, p + 4);
                if (len < 26) break;
                long reference = BitConverter.ToInt64(value, p + 16) & 0xFFFFFFFFFFFF;
                if (reference != rec.Number) extensions.Add(reference);
                p += len;
            }
            foreach (var ext in extensions)
            {
                var raw = ReadRecordRaw(ext);
                var er = raw == null ? null : Parse(raw, ext);
                if (er != null && er.BaseRecord == rec.Number) rec.Attributes.AddRange(er.Attributes);
            }
        }

        // An attribute split over several records appears once per piece: join the pieces' runs.
        var merged = new List<NtfsAttribute>();
        foreach (var g in rec.Attributes.GroupBy(a => (a.Type, a.Name)))
        {
            var parts = g.ToList();
            if (parts.Count == 1 || !parts[0].NonResident) { merged.AddRange(parts); continue; }
            var first = parts.OrderBy(a => a.Runs.Count > 0 ? a.Runs[0].Vcn : long.MaxValue).ToList();
            var head = first.FirstOrDefault(a => a.RealSize > 0 || a.Runs.Count > 0 && a.Runs[0].Vcn == 0) ?? first[0];
            var joined = new NtfsAttribute { Type = head.Type, Name = head.Name, NonResident = true, Flags = head.Flags };
            joined.RealSize = first.Max(a => a.RealSize);
            joined.InitializedSize = first.Max(a => a.InitializedSize);
            joined.CompressionUnit = first.Max(a => a.CompressionUnit);
            foreach (var part in first) joined.Runs.AddRange(part.Runs);
            merged.Add(joined);
        }
        rec.Attributes.Clear();
        rec.Attributes.AddRange(merged);
        return rec;
    }

    // ------------------------------------------------------------------ attribute data

    public byte[] ReadAll(NtfsAttribute a)
    {
        if (!a.NonResident) return a.Value ?? Array.Empty<byte>();
        if (a.RealSize > int.MaxValue) throw new InvalidDataException("Attribute too large to read at once.");
        var buf = new byte[a.RealSize];
        using var s = OpenAttribute(a);
        s.ReadExactly(buf);
        return buf;
    }

    /// <summary>A readable stream over an attribute's data (handles sparse, NTFS-compressed and uninitialized parts).</summary>
    public Stream OpenAttribute(NtfsAttribute a) => new NtfsAttributeStream(this, a);

    internal void ReadClusters(long lcn, long count, byte[] buffer, int index)
    {
        _r.Read(lcn * ClusterSize, buffer, index, checked((int)(count * ClusterSize)));
    }

    // ------------------------------------------------------------------ files and folders

    public static string? FileNameOf(NtfsRecord rec, out long parent)
    {
        parent = -1;
        string? best = null;
        int bestNs = -1;
        foreach (var a in rec.Attributes.Where(a => a.Type == NtfsAttribute.FileName && a.Value != null && a.Value.Length >= 66))
        {
            var v = a.Value!;
            int ns = v[65];
            if (ns == 2) { if (best == null) { best = Encoding.Unicode.GetString(v, 66, v[64] * 2); parent = BitConverter.ToInt64(v, 0) & 0xFFFFFFFFFFFF; } continue; }
            if (bestNs >= 0 && ns != 1 && ns != 3) continue;
            best = Encoding.Unicode.GetString(v, 66, v[64] * 2);
            parent = BitConverter.ToInt64(v, 0) & 0xFFFFFFFFFFFF;
            bestNs = ns;
        }
        return best;
    }

    /// <summary>Lists a folder's contents from its index ($I30), skipping DOS 8.3 duplicate names.</summary>
    public List<NtfsEntry> ListDirectory(long record, string path)
    {
        var rec = GetRecord(record) ?? throw new InvalidDataException("The folder's record couldn't be read.");
        var root = rec.Find(NtfsAttribute.IndexRoot, "$I30") ?? throw new InvalidDataException("That isn't a folder.");
        var alloc = rec.Find(NtfsAttribute.IndexAllocation, "$I30");
        var entries = new List<(NtfsEntry Entry, int Ns)>();
        var v = root.Value!;
        int recordSize = BitConverter.ToInt32(v, 8);
        if (recordSize <= 0) recordSize = IndexRecordSize;
        ReadIndexNode(v, 0x10, entries, path);

        if (alloc != null)
        {
            // Walk the B-tree from the root's child pointers so only live index blocks are read.
            var pending = new Stack<long>(ChildVcns(v, 0x10));
            var seen = new HashSet<long>();
            using var stream = OpenAttribute(alloc);
            long unit = ClusterSize <= recordSize ? ClusterSize : 512;
            var block = new byte[recordSize];
            while (pending.Count > 0)
            {
                long vcn = pending.Pop();
                if (!seen.Add(vcn)) continue;
                long off = vcn * unit;
                if (off + recordSize > alloc.RealSize) continue;
                stream.Position = off;
                stream.ReadExactly(block);
                if (Encoding.ASCII.GetString(block, 0, 4) != "INDX" || !ApplyFixups(block)) continue;
                ReadIndexNode(block, 0x18, entries, path);
                foreach (var c in ChildVcns(block, 0x18)) pending.Push(c);
            }
        }
        // A file with a long name also has a DOS 8.3 name entry: keep only the long one. (Hard links keep all names.)
        var hasLong = entries.Where(x => x.Ns != 2).Select(x => x.Entry.Record).ToHashSet();
        return entries.Where(x => x.Ns != 2 || !hasLong.Contains(x.Entry.Record))
            .Select(x => x.Entry)
            .GroupBy(e => (e.Record, e.Name)).Select(g => g.First())
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<(int Pos, int Len, int Flags)> IndexEntries(byte[] buf, int nodeHeader)
    {
        int first = nodeHeader + BitConverter.ToInt32(buf, nodeHeader);
        int end = Math.Min(nodeHeader + BitConverter.ToInt32(buf, nodeHeader + 4), buf.Length);
        for (int p = first; p + 16 <= end;)
        {
            int len = BitConverter.ToUInt16(buf, p + 8);
            int flags = BitConverter.ToInt32(buf, p + 12);
            if (len < 16 || p + len > buf.Length) yield break;
            yield return (p, len, flags);
            if ((flags & 2) != 0) yield break; // last entry
            p += len;
        }
    }

    private static IEnumerable<long> ChildVcns(byte[] buf, int nodeHeader)
    {
        foreach (var (p, len, flags) in IndexEntries(buf, nodeHeader).ToList())
            if ((flags & 1) != 0) yield return BitConverter.ToInt64(buf, p + len - 8);
    }

    private static void ReadIndexNode(byte[] buf, int nodeHeader, List<(NtfsEntry, int)> into, string path)
    {
        foreach (var (p, len, flags) in IndexEntries(buf, nodeHeader))
        {
            if ((flags & 2) != 0) continue; // the last entry carries no name
            int keyLen = BitConverter.ToUInt16(buf, p + 10);
            if (keyLen < 66) continue;
            int k = p + 16;
            int ns = buf[k + 65];
            long reference = BitConverter.ToInt64(buf, p) & 0xFFFFFFFFFFFF;
            if (reference < 16 && reference != RootRecord) continue; // $MFT, $Bitmap and the other metadata files
            int nameLen = buf[k + 64];
            string name = Encoding.Unicode.GetString(buf, k + 66, nameLen * 2);
            if (name is "." || reference == RootRecord) continue;
            uint rawAttrs = BitConverter.ToUInt32(buf, k + 56);
            bool isDir = (rawAttrs & 0x10000000) != 0; // "has a folder index"
            var fileAttrs = (FileAttributes)(rawAttrs & 0x0FFFFFFF) | (isDir ? FileAttributes.Directory : 0);
            into.Add((new NtfsEntry
            {
                Record = reference,
                Name = name,
                IsDirectory = isDir,
                Size = BitConverter.ToInt64(buf, k + 48),
                ModifiedUtc = FromFileTime(BitConverter.ToInt64(buf, k + 16)),
                Attributes = fileAttrs,
                Path = path.TrimEnd('\\') + "\\" + name,
                ReparseTag = fileAttrs.HasFlag(FileAttributes.ReparsePoint) ? BitConverter.ToUInt32(buf, k + 60) : 0,
            }, ns));
        }
    }

    public static DateTime FromFileTime(long ft) =>
        ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc() ? DateTime.MinValue : DateTime.FromFileTimeUtc(ft);

    /// <summary>Finds an entry by path (e.g. \Users\amy), case-insensitively. Null if it isn't there.</summary>
    public NtfsEntry? Resolve(string path)
    {
        var current = new NtfsEntry { Record = RootRecord, Name = "", IsDirectory = true, Path = "\\" };
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.IsDirectory || current.IsReparsePoint) return null;
            var next = ListDirectory(current.Record, current.Path).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Opens a file's contents. Handles Windows per-file compression (WOF, used by "compact /exe" and
    /// CompactOS); throws <see cref="NotSupportedException"/> with a reason for data DiskImager can't read.
    /// </summary>
    public Stream OpenFile(long record)
    {
        var rec = GetRecord(record) ?? throw new InvalidDataException("the file's record couldn't be read");
        var main = rec.Find(NtfsAttribute.Data);
        var reparse = rec.Find(NtfsAttribute.ReparsePoint);
        if (reparse != null)
        {
            var v = ReadAll(reparse);
            uint tag = v.Length >= 4 ? BitConverter.ToUInt32(v, 0) : 0;
            if (tag == TagWof && v.Length >= 24)
            {
                uint provider = BitConverter.ToUInt32(v, 12);
                var wof = rec.Find(NtfsAttribute.Data, "WofCompressedData");
                if (provider == 2 && wof != null) return new WofStream(OpenAttribute(wof), main?.RealSize ?? 0, BitConverter.ToInt32(v, 20));
                throw new NotSupportedException("stored inside a Windows image file (WIMBoot)");
            }
            if (tag == TagDedup) throw new NotSupportedException("deduplicated by Windows Server, so its data is stored elsewhere");
            if (tag is TagMountPoint or TagSymlink) throw new NotSupportedException("a link to another location, not a file");
        }
        if (main == null) throw new InvalidDataException("the file has no data");
        return OpenAttribute(main);
    }

    /// <summary>Accurate size, times and attributes from the file's own record (directory listings can lag behind).</summary>
    public void Refresh(NtfsEntry e)
    {
        var rec = GetRecord(e.Record);
        if (rec == null) return;
        var si = rec.Find(NtfsAttribute.StandardInformation);
        if (si?.Value is { Length: >= 0x24 } v)
        {
            e.ModifiedUtc = FromFileTime(BitConverter.ToInt64(v, 8));
            e.Attributes = (FileAttributes)BitConverter.ToUInt32(v, 0x20) | (e.IsDirectory ? FileAttributes.Directory : 0);
        }
        if (!e.IsDirectory)
        {
            var data = rec.Find(NtfsAttribute.Data);
            if (data != null) e.Size = data.RealSize;
        }
    }
}
