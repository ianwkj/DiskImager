using System.Collections.Concurrent;
using DiskImager.Core;

namespace DiskImager.Rescue.Platform;

/// <summary>
/// Writes an image onto a whole target disk with a layout plan (partitions may move and grow): the same
/// steps as DiskImager's Windows writer, through plain file I/O, so it works on a Linux block device
/// (/dev/sda) and, for tests, on a disk image file. Growing the file systems is left to the caller.
/// </summary>
public static class RestoreWriter
{
    public static void Write(ImageReader src, string targetPath, long targetSize, int targetSectorSize, LayoutPlan plan, bool newIds,
        IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        if (src.SectorSize != targetSectorSize)
            throw new InvalidOperationException($"Sector size mismatch: the backup uses {src.SectorSize}-byte sectors, the disk {targetSectorSize}-byte sectors.");
        if (plan.TargetSize != targetSize) throw new InvalidOperationException("The layout was planned for a different disk.");
        if (plan.NeedsShrink)
            throw new InvalidOperationException("This disk is smaller than the backup's partitions. Shrinking isn't available in the rescue system: choose a disk at least as big, or restore from Windows.");
        if (plan.Validate() is string problem) throw new InvalidOperationException(problem);

        int bs = src.BlockSize;
        var blocks = src.Blocks;
        if (blocks.Count == 0 || blocks[0] != 0) throw new InvalidDataException("The backup has no partition table area.");
        var block0 = new byte[bs];
        src.ReadBlock(0, block0, out int len0);
        long firstPart = plan.Parts.Min(p => p.Source.Offset);
        var final = PartitionTable.Build(block0[..(int)Math.Min(len0, firstPart)], plan, newIds);

        var parts = plan.Parts;
        var needed = blocks.Where(b =>
        {
            long s = b * bs, e = s + bs;
            return parts.Any(p => s < p.Source.Offset + Math.Min(p.Source.Length, p.TargetLength) && e > p.Source.Offset);
        }).ToList();

        foreach (var p in parts.Where(p => p.TargetOffset != p.Source.Offset || p.TargetLength != p.Source.Length))
            log.Info($"{p.Source.Name}: {Util.FormatBytes(p.Source.Length)} -> {Util.FormatBytes(p.TargetLength)}" +
                     (p.TargetOffset != p.Source.Offset ? $" (moved to {Util.FormatBytes(p.TargetOffset)} from the start)" : ""));
        log.Info($"Writing to {targetPath}. All existing data on it is being replaced.");

        using var dev = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1 << 20);
        void WriteAt(long offset, byte[] data, int index, int count)
        {
            dev.Position = offset;
            dev.Write(data, index, count);
        }

        // Clear the old partition tables (start and end of the disk) first.
        var zero = new byte[1 << 20];
        int z = (int)Math.Min(zero.Length, targetSize);
        WriteAt(0, zero, 0, z);
        WriteAt(targetSize - z, zero, 0, z);

        var tracker = new ProgressTracker(progress, "Restoring", (long)needed.Count * bs);
        using var queue = new BlockingCollection<(long Index, byte[] Data, int Length)>(boundedCapacity: 8);
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() =>
        {
            try
            {
                foreach (var b in needed)
                {
                    readerCts.Token.ThrowIfCancellationRequested();
                    var buf = new byte[bs];
                    src.ReadBlock(b, buf, out int len);
                    queue.Add((b, buf, len), readerCts.Token);
                }
            }
            finally { queue.CompleteAdding(); }
        });

        long done = 0;
        try
        {
            foreach (var (index, data, length) in queue.GetConsumingEnumerable(ct))
            {
                long off = index * bs, end = off + length;
                foreach (var p in parts)
                {
                    long s = Math.Max(off, p.Source.Offset);
                    long e = Math.Min(end, p.Source.Offset + Math.Min(p.Source.Length, p.TargetLength));
                    if (e > s) WriteAt(p.TargetOffset + (s - p.Source.Offset), data, (int)(s - off), (int)(e - s));
                }
                done += bs;
                tracker.Report(done);
            }
            reader.GetAwaiter().GetResult();
        }
        catch
        {
            readerCts.Cancel();
            try { reader.Wait(); } catch { }
            log.Warn("Writing stopped part-way: the disk is now incomplete and should be restored again.");
            throw;
        }

        // Partition table last, so the system only sees the new partitions once their data is in place.
        WriteAt(0, final.Primary, 0, final.Primary.Length);
        if (final.Backup != null) WriteAt(final.BackupOffset, final.Backup, 0, final.Backup.Length);
        dev.Flush(flushToDisk: true);
        tracker.Report(done, force: true);
        log.Info($"Finished writing {targetPath}.");
    }
}
