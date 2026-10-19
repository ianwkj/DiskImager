using System.Runtime.InteropServices;

namespace DiskImager.Core;

/// <summary>
/// A disk expressed as fixed-size blocks at absolute disk offsets. Only "used" blocks are listed;
/// anything not listed is free space and never needs to be copied.
/// Implemented by a live disk (backup / clone) and by an image file (restore).
/// </summary>
public interface IBlockSource : IDisposable
{
    int BlockSize { get; }
    long DiskSize { get; }
    int SectorSize { get; }
    DiskStyle Style { get; }
    long MaxPartitionEnd { get; }
    IReadOnlyList<long> Blocks { get; }
    /// <summary>Fills buffer[0..length) with the block's bytes.</summary>
    void ReadBlock(long blockIndex, byte[] buffer, out int length);
}

/// <summary>Reads a physical disk that may be in use, via VSS snapshots, skipping free NTFS clusters.</summary>
public sealed class LiveDiskSource : IBlockSource
{
    public const int DefaultBlockSize = 1 << 20;
    private const long MaxRawGap = 64L << 20;

    private sealed class Region
    {
        public long Start, Length;
        public RawDevice Reader = null!;
        public long ReaderBase;
        public string Name = "";
    }

    private readonly RawDevice _disk;
    private readonly List<Region> _regions = new();
    private readonly List<IDisposable> _owned = new();
    private readonly Logger _log;
    private readonly HashSet<string> _fallbackWarned = new();
    private readonly IReadOnlyList<string> _exclude;

    public DiskInfo Disk { get; }
    /// <summary>Folders whose files were left out ("Exclude folders"), to be emptied after a restore.</summary>
    public List<ImageExclusion> Exclusions { get; } = new();
    /// <summary>Change-tracking scans of the snapshotted NTFS volumes (when requested).</summary>
    public List<(PartitionInfo Partition, Ntfs.ChangeTracker.Scan Scan)> TrackingScans { get; } = new();
    private bool _track;
    public int BlockSize { get; } = DefaultBlockSize;
    public long DiskSize => Disk.Size;
    public int SectorSize => Disk.SectorSize;
    public DiskStyle Style => Disk.Style;
    public long MaxPartitionEnd => Disk.MaxPartitionEnd;
    public IReadOnlyList<long> Blocks { get; private set; } = Array.Empty<long>();

    private LiveDiskSource(DiskInfo disk, IReadOnlyList<string>? exclude, Logger log)
    {
        Disk = disk;
        _log = log;
        _exclude = exclude ?? Array.Empty<string>();
        _disk = RawDevice.Open(disk.Path, write: false);
    }

    /// <param name="exclude">"Exclude folders" patterns such as C:\Users\*\Downloads.</param>
    /// <param name="track">Scan each snapshotted NTFS volume for change tracking (faster incrementals).</param>
    public static LiveDiskSource Open(DiskInfo disk, bool useVss, Logger log, CancellationToken ct, IReadOnlyList<string>? exclude = null, bool track = false)
    {
        var src = new LiveDiskSource(disk, exclude, log) { _track = track };
        try
        {
            src.Build(useVss, ct);
            return src;
        }
        catch
        {
            src.Dispose();
            throw;
        }
    }

    private void Build(bool useVss, CancellationToken ct)
    {
        long totalBlocks = (DiskSize + BlockSize - 1) / BlockSize;
        var used = new bool[totalBlocks];
        var parts = Disk.DataPartitions.ToList();

        // Partition table / boot area at the start of the disk.
        long firstStart = parts.Count > 0 ? parts.Min(p => p.Offset) : DiskSize;
        long lead = Math.Min(firstStart, 1L << 20);
        if (lead <= 0) lead = Math.Min(DiskSize, 1L << 20);
        AddRaw(0, lead, "partition table", used);

        // MBR: logical partitions keep their EBR records in the small gaps between partitions.
        if (Style == DiskStyle.MBR)
        {
            long pos = lead;
            foreach (var p in parts)
            {
                if (p.Offset > pos && p.Offset - pos <= MaxRawGap) AddRaw(pos, p.Offset - pos, "gap", used);
                pos = Math.Max(pos, p.End);
            }
        }

        if (Style == DiskStyle.RAW && parts.Count == 0)
            AddRaw(0, DiskSize, "whole disk", used);

        foreach (var p in parts)
        {
            ct.ThrowIfCancellationRequested();
            AddPartition(p, useVss, used);
        }

        var list = new List<long>();
        for (long i = 0; i < totalBlocks; i++) if (used[i]) list.Add(i);
        Blocks = list;
        _log.Info($"Disk {Disk.Number}: {list.Count:N0} blocks in use ({Util.FormatBytes((double)list.Count * BlockSize)}) of {Util.FormatBytes(DiskSize)}.");
    }

    private void AddRaw(long start, long length, string name, bool[] used)
    {
        _regions.Add(new Region { Start = start, Length = length, Reader = _disk, ReaderBase = 0, Name = name });
        MarkRange(used, start, start + length);
    }

    private void AddPartition(PartitionInfo p, bool useVss, bool[] used)
    {
        var vol = p.Volume;
        string name = vol?.DriveLetter ?? $"partition {p.Number} ({p.TypeName})";
        var region = new Region { Start = p.Offset, Length = p.Length, Reader = _disk, ReaderBase = 0, Name = name };
        RawDevice? bitmapDevice = null;

        bool snapshotable = vol?.DriveLetter != null && vol.FileSystem is "NTFS" or "ReFS";
        if (useVss && snapshotable)
        {
            try
            {
                var snap = VssSnapshot.Create(vol!.DriveLetter + "\\");
                _owned.Add(snap);
                var dev = RawDevice.Open(snap.DevicePath, write: false);
                _owned.Add(dev);
                region.Reader = dev;
                region.ReaderBase = p.Offset;
                bitmapDevice = dev;
                _log.Info($"{name}: snapshot created ({snap.DevicePath}).");
            }
            catch (Exception ex)
            {
                _log.Warn($"{name}: snapshot failed, reading the live volume instead ({ex.Message}). Files changing during the backup may be inconsistent.");
            }
        }

        bool mapped = false;
        if (vol?.FileSystem == "NTFS")
        {
            try
            {
                if (bitmapDevice == null)
                {
                    bitmapDevice = RawDevice.Open(vol.DevicePath, write: false);
                    _owned.Add(bitmapDevice);
                }
                long cluster = ClusterSize(vol);
                mapped = cluster > 0 && MarkUsedClusters(bitmapDevice, p, cluster, used);
            }
            catch (Exception ex)
            {
                _log.Warn($"{name}: could not read the free-space map ({ex.Message}); copying every sector.");
            }
        }

        if (!mapped) MarkRange(used, p.Offset, p.End);
        if (vol?.DriveLetter != null && vol.FileSystem == "NTFS")
        {
            string? snapshotRoot = region.Reader != _disk ? region.Reader.Path : null;
            SkipSystemFiles(p, vol, snapshotRoot, used);
            if (mapped) SkipExcludedFolders(p, vol, snapshotRoot, used);
            if (_track && snapshotRoot != null && p.Offset % BlockSize == 0)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ntfs = new Ntfs.NtfsVolume(new Ntfs.DeviceVolumeReader(region.Reader, p.Length));
                    TrackingScans.Add((p, Ntfs.ChangeTracker.ScanVolume(ntfs, BlockSize, p.Length)));
                    _log.Info($"{name}: change tracking map built ({ntfs.MftRecordCount:N0} file records, {sw.Elapsed.TotalSeconds:0.0} s).");
                }
                catch (Exception ex) { _log.Info($"{name}: change tracking unavailable this time ({ex.Message}); every used block will be read."); }
            }
        }
        _regions.Add(region);
    }

    /// <summary>
    /// Leaves out the data of every file inside the job's excluded folders on this volume. The folders are
    /// looked up in the snapshot (so the list matches what's being imaged) and recorded in the image, so a
    /// restore can empty them: their files' data was never saved.
    /// </summary>
    private void SkipExcludedFolders(PartitionInfo p, VolumeInfo vol, string? snapshotRoot, bool[] used)
    {
        var patterns = _exclude.Where(x => x.Length > 3 && string.Equals(x[..2], vol.DriveLetter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (patterns.Count == 0) return;
        long cluster = ClusterSize(vol);
        if (cluster <= 0) return;
        string root = snapshotRoot ?? vol.DriveLetter!;
        long total = 0;
        int files = 0, unreadable = 0;
        foreach (var pattern in patterns)
        {
            var folders = Core.Exclusions.Expand(root, pattern[2..]);
            if (folders.Count == 0) _log.Info($"Exclude {pattern}: no matching folder on this disk.");
            foreach (var rel in folders)
            {
                if (Exclusions.Any(e => e.PartitionOffset == p.Offset && string.Equals(e.Folder, rel, StringComparison.OrdinalIgnoreCase))) continue;
                Exclusions.Add(new ImageExclusion { PartitionOffset = p.Offset, Folder = rel });
                foreach (var file in Core.Exclusions.FilesBelow(root + rel))
                {
                    try
                    {
                        total += UnmarkExtents(used, p.Offset, cluster, BlockSize, FileExtents(file), 0);
                        files++;
                    }
                    catch { unreadable++; } // small files live inside the MFT and have no data runs; locked ones are left in
                }
            }
        }
        if (Exclusions.Count > 0)
            _log.Info($"{vol.DriveLetter}: excluding {Exclusions.Count(e => e.PartitionOffset == p.Offset)} folder(s), {files:N0} files; " +
                      $"saved {Util.FormatBytes(total)}{(unreadable > 0 ? $" ({unreadable:N0} small or locked files stay in the image)" : "")}.");
    }

    /// <summary>Windows recreates these at boot, so their contents never need backing up.</summary>
    public static readonly string[] SkippedFiles = { "pagefile.sys", "hiberfil.sys", "swapfile.sys" };

    /// <summary>
    /// Removes blocks that hold only the page, hibernation or swap file from the backup. Windows keeps the live
    /// files locked against every other program, so they're looked up in the volume's snapshot, where they
    /// aren't in use (and if the snapshot doesn't contain them, Windows has already left them out).
    /// </summary>
    private void SkipSystemFiles(PartitionInfo p, VolumeInfo vol, string? snapshotDevice, bool[] used)
    {
        long cluster = ClusterSize(vol);
        if (cluster <= 0) return;
        var skipped = new List<string>();
        long total = 0;
        foreach (var name in SkippedFiles)
        {
            if (!File.Exists(vol.DriveLetter + "\\" + name)) continue;
            List<(long, long, long)>? extents = null;
            foreach (var root in new[] { snapshotDevice, vol.DriveLetter }.Where(r => r != null))
            {
                try { extents = FileExtents(root + "\\" + name); break; }
                catch { /* not in the snapshot, or locked: try the next place */ }
            }
            if (extents == null)
            {
                _log.Info($"{vol.DriveLetter}: {name} is in use and couldn't be located, so it's included in this backup.");
                continue;
            }
            long bytes = UnmarkExtents(used, p.Offset, cluster, BlockSize, extents, KeepStartOfSkippedFile);
            if (bytes <= 0) continue;
            total += bytes;
            skipped.Add($"{name} ({Util.FormatBytes(bytes)})");
        }
        if (skipped.Count > 0)
            _log.Info($"{vol.DriveLetter}: skipping {string.Join(", ", skipped)}; Windows recreates them when it starts. Saved {Util.FormatBytes(total)}.");
    }

    /// <summary>
    /// The first part of each skipped file is still copied. For hiberfil.sys that keeps the header saying
    /// "not hibernating", so a restored disk can never try to resume from stale data left on the target.
    /// </summary>
    public const long KeepStartOfSkippedFile = 1L << 20;

    /// <summary>Where a file's data sits on its volume: (file cluster, volume cluster, cluster count) runs.</summary>
    public static List<(long Vcn, long Lcn, long Clusters)> FileExtents(string path)
    {
        using var h = Native.CreateFileW(path, Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var result = new List<(long, long, long)>();
        var outBuf = new byte[64 * 1024];
        long vcn = 0;
        while (true)
        {
            bool ok = Native.DeviceIoControl(h, Native.FSCTL_GET_RETRIEVAL_POINTERS, BitConverter.GetBytes(vcn), 8,
                outBuf, outBuf.Length, out int ret, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != Native.ERROR_MORE_DATA)
            {
                if (err == 38 /* ERROR_HANDLE_EOF: empty file */) break;
                throw new System.ComponentModel.Win32Exception(err);
            }
            int count = BitConverter.ToInt32(outBuf, 0);
            long start = BitConverter.ToInt64(outBuf, 8);
            for (int i = 0; i < count; i++)
            {
                long next = BitConverter.ToInt64(outBuf, 16 + i * 16);
                long lcn = BitConverter.ToInt64(outBuf, 24 + i * 16);
                result.Add((start, lcn, next - start));
                start = next;
            }
            if (ok || count == 0) break;
            vcn = start;
        }
        return result;
    }

    /// <summary>
    /// Clears "used" blocks lying entirely inside the given file runs, leaving the file's first
    /// <paramref name="keepFirstBytes"/> alone. Returns the number of bytes skipped.
    /// </summary>
    public static long UnmarkExtents(bool[] used, long partOffset, long clusterSize, int blockSize,
        IEnumerable<(long Vcn, long Lcn, long Clusters)> extents, long keepFirstBytes)
    {
        long skipped = 0;
        foreach (var (vcn, lcn, clusters) in extents)
        {
            if (lcn < 0 || clusters <= 0) continue; // sparse / not on disk
            long fileStart = vcn * clusterSize, fileEnd = fileStart + clusters * clusterSize;
            long trim = Math.Max(0, Math.Min(fileEnd, keepFirstBytes) - fileStart);
            long start = partOffset + lcn * clusterSize + trim;
            long end = partOffset + (lcn + clusters) * clusterSize;
            long firstBlock = (start + blockSize - 1) / blockSize; // only blocks fully inside the run
            long lastBlockExcl = end / blockSize;
            for (long b = firstBlock; b < lastBlockExcl && b < used.Length; b++)
            {
                if (!used[b]) continue;
                used[b] = false;
                skipped += blockSize;
            }
        }
        return skipped;
    }

    private static long ClusterSize(VolumeInfo vol) =>
        Native.GetDiskFreeSpaceW(vol.GuidPath, out uint spc, out uint bps, out _, out _) ? (long)spc * bps : 0;

    /// <summary>Marks blocks containing allocated NTFS clusters, using FSCTL_GET_VOLUME_BITMAP.</summary>
    private bool MarkUsedClusters(RawDevice vol, PartitionInfo p, long clusterSize, bool[] used)
    {
        const int chunk = 1 << 20; // bitmap bytes per call (8M clusters)
        var outBuf = new byte[16 + chunk];
        var inBuf = new byte[8];
        long lcn = 0, totalClusters = 0;
        int bs = BlockSize;

        while (true)
        {
            BitConverter.TryWriteBytes(inBuf, lcn);
            bool ok = Native.DeviceIoControl(vol.Handle, Native.FSCTL_GET_VOLUME_BITMAP, inBuf, 8, outBuf, outBuf.Length, out int ret, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != Native.ERROR_MORE_DATA) return false;

            long startLcn = BitConverter.ToInt64(outBuf, 0);
            long bitmapSize = BitConverter.ToInt64(outBuf, 8);
            totalClusters = startLcn + bitmapSize;
            long here = Math.Min(bitmapSize, (long)(ret - 16) * 8);

            for (long c = 0; c < here;)
            {
                int b = outBuf[16 + (c >> 3)];
                if (b == 0 && (c & 7) == 0) { c += 8; continue; }
                if (((b >> (int)(c & 7)) & 1) == 0) { c++; continue; }

                long byteOff = p.Offset + (startLcn + c) * clusterSize;
                long first = byteOff / bs, last = (byteOff + clusterSize - 1) / bs;
                for (long k = first; k <= last && k < used.Length; k++) used[k] = true;
                // Jump to the first cluster that reaches into the next unmarked block.
                long nextLcn = ((last + 1) * bs - p.Offset) / clusterSize - startLcn;
                c = Math.Max(c + 1, nextLcn);
            }

            if (ok || here == 0) break;
            lcn = startLcn + here;
        }

        // Anything after the last cluster (e.g. the NTFS backup boot sector) is always copied.
        long fsEnd = p.Offset + totalClusters * clusterSize;
        MarkRange(used, Math.Min(fsEnd, p.End), p.End);
        MarkRange(used, p.Offset, p.Offset + Math.Min(p.Length, 64 * 1024));
        return true;
    }

    private void MarkRange(bool[] used, long start, long end)
    {
        if (end <= start) return;
        long first = start / BlockSize, last = (end - 1) / BlockSize;
        for (long k = first; k <= last && k < used.Length; k++) used[k] = true;
    }

    public void ReadBlock(long blockIndex, byte[] buffer, out int length)
    {
        long off = blockIndex * BlockSize;
        length = (int)Math.Min(BlockSize, DiskSize - off);
        Array.Clear(buffer, 0, length);
        long end = off + length;

        foreach (var r in _regions)
        {
            long s = Math.Max(off, r.Start), e = Math.Min(end, r.Start + r.Length);
            if (e <= s) continue;
            try
            {
                r.Reader.ReadAt(s - r.ReaderBase, buffer, (int)(s - off), (int)(e - s));
            }
            catch (IOException) when (r.Reader != _disk)
            {
                if (_fallbackWarned.Add(r.Name))
                    _log.Warn($"{r.Name}: snapshot read failed near offset {s}; reading that area from the disk directly.");
                _disk.ReadAt(s, buffer, (int)(s - off), (int)(e - s));
            }
        }
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _disk.Dispose();
    }
}
