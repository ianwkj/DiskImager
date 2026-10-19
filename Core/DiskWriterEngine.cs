using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskImager.Core;

/// <summary>Writes a block source (an image, or a live disk when cloning) onto a whole target disk.</summary>
public static class DiskWriterEngine
{
    /// <summary>Copies with the source's exact partition layout.</summary>
    public static void Write(IBlockSource src, DiskInfo target, bool newIds, string stage,
        IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        CheckTarget(src, target);
        if (src.MaxPartitionEnd > target.Size)
            throw new InvalidOperationException($"The target disk is too small: it needs at least {Util.FormatBytes(src.MaxPartitionEnd)}.");

        var blocks = src.Blocks;
        if (blocks.Count == 0 || blocks[0] != 0)
            throw new InvalidDataException("The source has no partition table area.");

        int bs = src.BlockSize;
        var block0 = new byte[bs];
        src.ReadBlock(0, block0, out int len0);

        Gpt.Fixup? gpt = null;
        if (src.Style == DiskStyle.GPT)
            gpt = Gpt.ForTarget(block0, len0, src.SectorSize, target.Size, newIds);
        else if (src.Style == DiskStyle.MBR && newIds)
            BitConverter.TryWriteBytes(block0.AsSpan(440), (uint)Random.Shared.Next(1, int.MaxValue));

        log.Info($"Writing to {target.Title}. All existing data on it is being replaced.");

        using (var dev = TargetDisk.Open(target, log, clearLayout: true))
        {
            Wipe(dev, target.Size, log);
            var tracker = new ProgressTracker(progress, stage, (long)blocks.Count * bs);
            long done = Copy(src, blocks.Where(b => b != 0 && b * (long)bs < target.Size).ToList(), ct, log, (index, data, length) =>
            {
                long off = index * bs;
                dev.WriteAt(off, data, 0, (int)Math.Min(length, target.Size - off));
            }, tracker);

            // Partition table last, so Windows only sees the new partitions once their data is in place.
            dev.WriteAt(0, block0, 0, (int)Math.Min(len0, target.Size));
            if (gpt != null) dev.WriteAt(gpt.BackupOffset, gpt.Backup, 0, gpt.Backup.Length);
            dev.Flush();
            UpdateProperties(dev.Handle, log);
            tracker.Report(done + bs, force: true);
        }

        log.Info($"Finished writing {target.Title}.");
        if (target.Size > src.MaxPartitionEnd + (1L << 30))
            log.Info($"The target has {Util.FormatBytes(target.Size - src.MaxPartitionEnd)} of unallocated space.");
    }

    /// <summary>
    /// Copies with a new layout: partitions may move and grow (shrinking is done beforehand by
    /// <see cref="DiskCopy"/>). Grown NTFS/ReFS file systems are extended by Windows afterwards.
    /// </summary>
    /// <param name="tableOverride">Partition table bytes to use instead of the source's own
    /// (used when the source is a staging copy that carries temporary disk IDs).</param>
    /// <param name="emptyAfter">Folders excluded from the image: emptied on the target once it's written.</param>
    public static void Write(IBlockSource src, DiskInfo target, LayoutPlan plan, bool newIds, string stage,
        IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log, byte[]? tableOverride = null,
        IReadOnlyList<ImageExclusion>? emptyAfter = null)
    {
        bool isDisk = target.Path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase);
        bool cleanup = emptyAfter is { Count: > 0 } && isDisk;
        if (plan.IsIdentity && tableOverride == null && !cleanup)
        {
            Write(src, target, newIds, stage, progress, ct, log);
            return;
        }

        CheckTarget(src, target);
        if (plan.TargetSize != target.Size) throw new InvalidOperationException("The layout was planned for a different target disk.");
        string? problem = plan.Validate();
        if (problem != null) throw new InvalidOperationException(problem);
        if (plan.NeedsShrink) throw new InvalidOperationException("Shrinking partitions must go through a staging copy.");

        int bs = src.BlockSize;
        byte[] table = tableOverride ?? ReadTableArea(src, plan);
        var final = PartitionTable.Build(table, plan, newIds);
        // Extending and emptying excluded folders need Windows to mount the new volumes. If the copy keeps the
        // source's disk IDs while the source is still attached, Windows would hide it (signature collision),
        // so mount it with temporary IDs first.
        bool extend = plan.NeedsExtend;
        bool mount = extend || cleanup;
        var first = mount && !newIds ? PartitionTable.Build(table, plan, newIds: true) : final;

        foreach (var p in plan.Parts.Where(p => p.TargetOffset != p.Source.Offset || p.TargetLength != p.Source.Length))
            log.Info($"{p.Source.Name}: {Util.FormatBytes(p.Source.Length)} -> {Util.FormatBytes(p.TargetLength)}" +
                     (p.TargetOffset != p.Source.Offset ? $" (moved to {Util.FormatBytes(p.TargetOffset)} from the start)" : ""));
        log.Info($"Writing to {target.Title}. All existing data on it is being replaced.");

        var parts = plan.Parts;
        var needed = src.Blocks.Where(b =>
        {
            long s = b * bs, e = s + bs;
            return parts.Any(p => s < p.Source.Offset + Math.Min(p.Source.Length, p.TargetLength) && e > p.Source.Offset);
        }).ToList();

        using (var dev = TargetDisk.Open(target, log, clearLayout: true))
        {
            Wipe(dev, target.Size, log);
            var tracker = new ProgressTracker(progress, stage, (long)needed.Count * bs);
            long done = Copy(src, needed, ct, log, (index, data, length) =>
            {
                long off = index * bs, end = off + length;
                foreach (var p in parts)
                {
                    long s = Math.Max(off, p.Source.Offset);
                    long e = Math.Min(end, p.Source.Offset + Math.Min(p.Source.Length, p.TargetLength));
                    if (e > s) dev.WriteAt(p.TargetOffset + (s - p.Source.Offset), data, (int)(s - off), (int)(e - s));
                }
            }, tracker);

            WriteTable(dev, first);
            dev.Flush();
            UpdateProperties(dev.Handle, log);
            tracker.Report(done, force: true);
        }

        if (mount)
        {
            if (extend)
            {
                progress?.Report(new ProgressInfo("Extending file systems", 0, 0, 0));
                ExtendFileSystems(target, plan, log, ct);
            }
            if (cleanup)
            {
                progress?.Report(new ProgressInfo("Emptying excluded folders", 0, 0, 0));
                EmptyExcludedFolders(target, plan, emptyAfter!, log, ct);
            }
            if (!ReferenceEquals(first, final))
            {
                using var dev = TargetDisk.Open(target, log, clearLayout: false);
                WriteTable(dev, final);
                dev.Flush();
                UpdateProperties(dev.Handle, log);
            }
        }
        log.Info($"Finished writing {target.Title}.");
        if (plan.UsableEnd - plan.End > (1L << 30))
            log.Info($"{Util.FormatBytes(plan.UsableEnd - plan.End)} of the target is left unallocated.");
    }

    private static void CheckTarget(IBlockSource src, DiskInfo target)
    {
        if (target.IsSystem)
            throw new InvalidOperationException("You can't overwrite the disk Windows is currently running from.");
        if (src.SectorSize != target.SectorSize)
            throw new InvalidOperationException($"Sector size mismatch: source uses {src.SectorSize}-byte sectors, target uses {target.SectorSize}-byte sectors.");
    }

    public static byte[] ReadTableArea(IBlockSource src, LayoutPlan plan)
    {
        var block0 = new byte[src.BlockSize];
        src.ReadBlock(0, block0, out int len0);
        long first = plan.Parts.Min(p => p.Source.Offset);
        return block0[..(int)Math.Min(len0, first)];
    }

    private static void WriteTable(TargetDisk dev, PartitionTable.Image t)
    {
        dev.WriteAt(0, t.Primary, 0, t.Primary.Length);
        if (t.Backup != null) dev.WriteAt(t.BackupOffset, t.Backup, 0, t.Backup.Length);
    }

    /// <summary>Removes the old partition tables (start and end of disk) so Windows releases its volumes.</summary>
    private static void Wipe(TargetDisk dev, long size, Logger log)
    {
        var zero = new byte[1 << 20];
        int z = (int)Math.Min(zero.Length, size);
        dev.WriteAt(0, zero, 0, z);
        dev.WriteAt(size - z, zero, 0, z);
        dev.Flush();
        UpdateProperties(dev.Handle, log);
    }

    /// <summary>Reads blocks on a background task while the caller writes them.</summary>
    private static long Copy(IBlockSource src, List<long> blocks, CancellationToken ct, Logger log,
        Action<long, byte[], int> write, ProgressTracker tracker)
    {
        int bs = src.BlockSize;
        using var queue = new BlockingCollection<(long Index, byte[] Data, int Length)>(boundedCapacity: 8);
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = Task.Run(() =>
        {
            try
            {
                foreach (var b in blocks)
                {
                    readerCts.Token.ThrowIfCancellationRequested();
                    var buf = new byte[bs];
                    src.ReadBlock(b, buf, out int len);
                    queue.Add((b, buf, len), readerCts.Token);
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        long done = 0;
        try
        {
            foreach (var (index, data, length) in queue.GetConsumingEnumerable(ct))
            {
                write(index, data, length);
                done += bs;
                tracker.Report(done);
            }
            readTask.GetAwaiter().GetResult();
        }
        catch
        {
            readerCts.Cancel();
            try { readTask.Wait(); } catch { }
            log.Warn("Writing stopped part-way: the target disk is now incomplete and should be restored again or reformatted.");
            throw;
        }
        return done;
    }

    /// <summary>Asks Windows to grow NTFS/ReFS to fill their enlarged partitions (like diskpart "extend filesystem").</summary>
    private static void ExtendFileSystems(DiskInfo target, LayoutPlan plan, Logger log, CancellationToken ct)
    {
        if (!target.Path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            log.Info("Target is a disk image file, not a disk: skipping file system extension.");
            return;
        }
        foreach (var p in plan.Parts.Where(p => p.Grows))
        {
            var v = WaitForVolume(target.Number, p.TargetOffset, TimeSpan.FromSeconds(45), ct);
            string manual = $"To finish it yourself: open Command Prompt as admin, run diskpart, then: select disk {target.Number}, select partition {p.Source.Number}, extend filesystem.";
            if (v == null)
            {
                log.Warn($"{p.Source.Name}: the partition was enlarged but Windows didn't mount it, so the file system still has its old size. {manual}");
                continue;
            }
            try
            {
                using var h = Native.Open(v.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE);
                long sectors = p.TargetLength / plan.SectorSize;
                if (TryExtend(h, sectors) || TryExtend(h, sectors - 1))
                    log.Info($"{p.Source.Name}: file system extended to {Util.FormatBytes(p.TargetLength)}.");
                else
                    log.Warn($"{p.Source.Name}: Windows refused to extend the file system ({new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}). {manual}");
            }
            catch (Exception ex)
            {
                log.Warn($"{p.Source.Name}: could not extend the file system ({ex.Message}). {manual}");
            }
        }
    }

    /// <summary>
    /// The files in excluded folders were never backed up: on the restored disk their entries would point at
    /// whatever the target held before. Empty those folders so nothing invalid is left behind.
    /// </summary>
    private static void EmptyExcludedFolders(DiskInfo target, LayoutPlan plan, IReadOnlyList<ImageExclusion> folders, Logger log, CancellationToken ct)
    {
        int removed = 0;
        foreach (var group in folders.GroupBy(f => f.PartitionOffset))
        {
            var part = plan.Parts.FirstOrDefault(p => p.Source.Offset == group.Key);
            if (part == null) continue;
            var v = WaitForVolume(target.Number, part.TargetOffset, TimeSpan.FromSeconds(45), ct);
            if (v == null)
            {
                log.Warn($"{part.Source.Name}: Windows didn't mount the restored partition, so these excluded folders couldn't be emptied: " +
                         $"{string.Join(", ", group.Select(f => f.Folder))}. Their files weren't backed up and hold no valid data; delete them before using the disk.");
                continue;
            }
            foreach (var f in group) removed += Exclusions.EmptyFolder(v.GuidPath + f.Folder.TrimStart('\\'), log);
        }
        log.Info($"Excluded folders emptied on the restored disk ({removed:N0} files that weren't backed up were removed).");
    }

    private static bool TryExtend(SafeFileHandle h, long sectors) =>
        Native.DeviceIoControl(h, Native.FSCTL_EXTEND_VOLUME, BitConverter.GetBytes(sectors), 8, null, 0, out _, IntPtr.Zero);

    public static VolumeInfo? WaitForVolume(int diskNumber, long offset, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var v = DiskEnumerator.GetVolumes().FirstOrDefault(v => v.DiskNumber == diskNumber && v.Offset == offset && v.FileSystem != "RAW");
            if (v != null || DateTime.UtcNow > until) return v;
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(1000);
        }
    }

    private static void UpdateProperties(SafeFileHandle h, Logger log)
    {
        if (!Native.TryIoctl(h, Native.IOCTL_DISK_UPDATE_PROPERTIES))
            log.Warn("Windows did not refresh the disk layout immediately; a rescan or reboot may be needed.");
    }

    /// <summary>
    /// A target disk opened for overwriting. Windows only lets a program write a disk's sectors when no
    /// mounted file system owns them, so this:
    ///  1. locks and dismounts every volume on the disk (keeping those handles open),
    ///  2. locks the disk itself,
    ///  3. optionally deletes the old partition layout so the volumes go away, and
    ///  4. if Windows still refuses a write inside a volume (common on USB sticks whose file system starts
    ///     at sector 0, with no partition table), writes that part through the locked volume instead.
    /// </summary>
    private sealed class TargetDisk : IDisposable
    {
        private sealed record LockedVolume(long Offset, long Length, string Name, SafeFileHandle Handle, RawDevice Device);

        private readonly RawDevice _disk;
        private readonly List<LockedVolume> _volumes = new();
        private readonly DiskInfo _info;
        private readonly Logger _log;
        private bool _usedFallback;

        public SafeFileHandle Handle => _disk.Handle;

        private TargetDisk(DiskInfo info, RawDevice disk, Logger log)
        {
            _info = info;
            _disk = disk;
            _log = log;
        }

        public static TargetDisk Open(DiskInfo target, Logger log, bool clearLayout)
        {
            var volumes = new List<LockedVolume>();
            foreach (var v in DiskEnumerator.GetVolumes().Where(v => v.DiskNumber == target.Number))
            {
                string name = v.DriveLetter ?? v.GuidPath;
                try
                {
                    var h = Native.Open(v.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE);
                    if (!Native.TryIoctl(h, Native.FSCTL_LOCK_VOLUME))
                        log.Warn($"{name} is in use by another program; forcing it offline.");
                    Native.TryIoctl(h, Native.FSCTL_DISMOUNT_VOLUME);
                    Native.TryIoctl(h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO);
                    volumes.Add(new LockedVolume(v.Offset, v.Length, name, h, RawDevice.Borrow(v.DevicePath, h)));
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not take {name} offline: {ex.Message}");
                }
            }

            RawDevice disk;
            try { disk = RawDevice.Open(target.Path, write: true); }
            catch
            {
                foreach (var v in volumes) v.Handle.Dispose();
                throw;
            }
            var t = new TargetDisk(target, disk, log);
            t._volumes.AddRange(volumes);

            Native.TryIoctl(disk.Handle, Native.FSCTL_LOCK_VOLUME);
            if (clearLayout && target.Path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                if (Native.TryIoctl(disk.Handle, Native.IOCTL_DISK_DELETE_DRIVE_LAYOUT))
                    Native.TryIoctl(disk.Handle, Native.IOCTL_DISK_UPDATE_PROPERTIES);
                else
                    log.Info("Windows didn't clear the old partition layout; overwriting it directly instead.");
            }
            return t;
        }

        public void WriteAt(long offset, byte[] buffer, int index, int count)
        {
            try
            {
                _disk.WriteAt(offset, buffer, index, count);
            }
            catch (DiskIOException ex) when (ex.Win32Error is 1 or 5)
            {
                if (!WriteThroughVolumes(offset, buffer, index, count)) throw Friendly(ex);
            }
            catch (DiskIOException ex) when (ex.Win32Error is 19)
            {
                throw new IOException($"Disk {_info.Number} is write-protected. If it's a USB stick or SD card, check for a lock switch, then try again.", ex);
            }
        }

        /// <summary>Writes the parts of a range that fall inside a locked volume through that volume's handle.</summary>
        private bool WriteThroughVolumes(long offset, byte[] buffer, int index, int count)
        {
            long end = offset + count;
            if (!_volumes.Any(v => v.Offset < end && v.Offset + v.Length > offset)) return false;
            if (!_usedFallback)
            {
                _usedFallback = true;
                _log.Info("Windows refused a direct disk write inside a volume; writing that area through the locked volume instead.");
            }

            long pos = offset;
            while (pos < end)
            {
                var vol = _volumes.FirstOrDefault(v => pos >= v.Offset && pos < v.Offset + v.Length);
                long stop;
                if (vol != null)
                {
                    stop = Math.Min(end, vol.Offset + vol.Length);
                    vol.Device.WriteAt(pos - vol.Offset, buffer, index + (int)(pos - offset), (int)(stop - pos));
                }
                else
                {
                    long next = _volumes.Where(v => v.Offset > pos).Select(v => v.Offset).DefaultIfEmpty(end).Min();
                    stop = Math.Min(end, next);
                    _disk.WriteAt(pos, buffer, index + (int)(pos - offset), (int)(stop - pos));
                }
                pos = stop;
            }
            return true;
        }

        private IOException Friendly(DiskIOException ex) => new(
            $"Windows refused to write to Disk {_info.Number} ({ex.Message.Split(": ").Last()}). " +
            "Close any programs or Explorer windows using that drive, unplug and reconnect it if it's removable, then try again.", ex);

        public void Flush()
        {
            _disk.Flush();
            foreach (var v in _volumes) Native.FlushFileBuffers(v.Handle);
        }

        public void Dispose()
        {
            _disk.Dispose();
            foreach (var v in _volumes) v.Handle.Dispose();
        }
    }
}
