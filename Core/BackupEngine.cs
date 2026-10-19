using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace DiskImager.Core;

public enum CompressionChoice { Fast, Better, None }

public sealed class BackupRequest
{
    public DiskInfo Disk { get; init; } = null!;
    public string DestinationFolder { get; init; } = "";
    public string JobName { get; init; } = "Backup";
    public bool Incremental { get; init; }
    public CompressionChoice Compression { get; init; } = CompressionChoice.Fast;
    public int MaxIncrementals { get; init; }
    public int KeepFullSets { get; init; }
    public bool UseVss { get; init; } = true;
    /// <summary>Leave deleting old sets to the caller (it runs clean-up after "verify image when complete").</summary>
    public bool DeferRetention { get; init; }
    /// <summary>"Exclude folders" patterns, e.g. C:\Users\*\Downloads.</summary>
    public IReadOnlyList<string> ExcludeFolders { get; init; } = Array.Empty<string>();
    /// <summary>Encrypt the image with this password (null or empty = not encrypted).</summary>
    public string? EncryptionPassword { get; init; }
}

public sealed class BackupResult
{
    public string ImagePath { get; init; } = "";
    public Guid ChainId { get; init; }
    public bool WasFull { get; init; }
    /// <summary>Set when the destination was close to full (within 20% of the estimate).</summary>
    public string? SpaceWarning { get; init; }
    public long BytesRead { get; init; }
    public long BytesWritten { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>True when "verify image when complete" ran and every block checked out.</summary>
    public bool Verified { get; set; }
}

public static class BackupEngine
{
    private const int Batch = 16;

    private sealed class BatchData
    {
        public int Count;
        public long[] Index = new long[Batch];
        public byte[][] Data = new byte[Batch][];
        public int[] Length = new int[Batch];
        public byte[][] Hash = new byte[Batch][];
        public IndexEntry?[] Ref = new IndexEntry?[Batch];
        public byte[]?[] Out = new byte[]?[Batch];
        public BlockFlags[] Flags = new BlockFlags[Batch];
    }

    public static BackupResult Run(BackupRequest req, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(req.DestinationFolder);
        CleanupPartials(req.DestinationFolder);

        if (DiskEnumerator.DiskNumberOfPath(req.DestinationFolder) == req.Disk.Number)
            log.Warn("The destination is on the disk being backed up. You'll need another disk to restore from it.");

        // Decide full vs incremental.
        ImageEntry? parentEntry = null;
        ImageChain? chain = null;
        if (req.Incremental)
        {
            chain = ImageCatalog.ChainsForJob(req.DestinationFolder, req.JobName)
                .FirstOrDefault(c => c.Images[0].Header.DiskId == req.Disk.Id);
            if (chain == null)
                log.Info("No earlier full backup of this disk in the destination: running a full backup instead.");
            else if (!chain.IsComplete || !chain.Images[0].Header.IsFull)
            { log.Warn("The latest backup set has missing files: starting a new full backup."); chain = null; }
            else if (chain.Images.Any(i => i.Damaged))
            { log.Warn("The latest backup set contains an image that failed verification: starting a new full backup."); chain = null; }
            else if (req.MaxIncrementals > 0 && chain.Images.Count - 1 >= req.MaxIncrementals)
            { log.Info($"This set already has {req.MaxIncrementals} incrementals: starting a new full backup."); chain = null; }
            else parentEntry = chain.Images[^1];
        }

        // Encryption: an incremental must use the same password (and so the same keys) as its set.
        bool encrypt = !string.IsNullOrEmpty(req.EncryptionPassword);
        ImageKey? key = null;
        if (parentEntry != null)
        {
            var pe = parentEntry.Header.Encryption;
            if (pe == null && encrypt)
            { log.Info("Encryption was turned on for this job: starting a new full backup."); parentEntry = null; }
            else if (pe != null && !encrypt)
            { log.Info("Encryption was turned off for this job: starting a new full backup."); parentEntry = null; }
            else if (pe != null)
            {
                try { key = ImageKey.Open(req.EncryptionPassword!, pe); }
                catch (WrongPasswordException) { log.Info("The backup password was changed: starting a new full backup."); parentEntry = null; }
            }
        }
        if (encrypt && key == null) key = ImageKey.Create(req.EncryptionPassword!);
        using var keyScope = key;

        using var parent = parentEntry != null ? new ImageReader(parentEntry.Path) : null;
        if (parent != null)
        {
            var missing = parent.MissingChainFiles();
            if (missing.Count > 0) throw new InvalidOperationException("Cannot add an incremental: " + missing[0]);
        }
        var parentMap = parent?.BuildMap();
        bool full = parent == null;

        log.Info($"Starting {(full ? "FULL" : "INCREMENTAL")} backup of {req.Disk.Title} to {req.DestinationFolder}");
        using var source = LiveDiskSource.Open(req.Disk, req.UseVss, log, ct, req.ExcludeFolders);
        string? spaceWarning = CheckFreeSpace(req, full, (long)source.Blocks.Count * source.BlockSize, chain, log);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int ordinal = full ? 0 : parent!.Header.ChainOrdinal + 1;
        string fileName = $"{Util.SafeName(req.JobName)}_{stamp}_{(full ? "F" : $"I{ordinal:D3}")}{ImageFormat.Extension}";
        var header = new ImageHeader
        {
            ImageId = Guid.NewGuid(),
            ChainId = full ? Guid.NewGuid() : parent!.Header.ChainId,
            IsFull = full,
            ChainOrdinal = ordinal,
            ChainFiles = full ? new List<string> { fileName } : parent!.Header.ChainFiles.Append(fileName).ToList(),
            CreatedUtc = DateTime.UtcNow,
            JobName = req.JobName,
            ComputerName = Environment.MachineName,
            BlockSize = source.BlockSize,
            DiskSize = req.Disk.Size,
            SectorSize = req.Disk.SectorSize,
            DiskId = req.Disk.Id,
            DiskModel = req.Disk.Model,
            Style = req.Disk.Style,
            UsedBytes = (long)source.Blocks.Count * source.BlockSize,
            Exclusions = source.Exclusions,
            Encryption = key?.Info,
            Partitions = req.Disk.Partitions.Select(p => new ImagePartition
            {
                Number = p.Number, Offset = p.Offset, Length = p.Length, IsContainer = p.IsContainer, Type = p.TypeName,
                FileSystem = p.Volume?.FileSystem ?? "", Label = p.Volume?.Label ?? "", DriveLetter = p.Volume?.DriveLetter,
            }).ToList(),
        };
        if (key != null) log.Info("The image is encrypted with the job's password (AES-256).");

        string path = System.IO.Path.Combine(req.DestinationFolder, fileName);
        using var writer = new ImageWriter(path, header);
        var level = req.Compression == CompressionChoice.Better ? CompressionLevel.Optimal : CompressionLevel.Fastest;
        var blocks = source.Blocks;
        var tracker = new ProgressTracker(progress, full ? "Creating full image" : "Creating incremental image", (long)blocks.Count * source.BlockSize);
        long bytesRead = 0, changed = 0;

        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var queue = new BlockingCollection<BatchData>(boundedCapacity: 3);
        var readTask = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < blocks.Count; i += Batch)
                {
                    readerCts.Token.ThrowIfCancellationRequested();
                    var b = new BatchData { Count = Math.Min(Batch, blocks.Count - i) };
                    for (int k = 0; k < b.Count; k++)
                    {
                        b.Index[k] = blocks[i + k];
                        b.Data[k] = ArrayPool<byte>.Shared.Rent(source.BlockSize);
                        source.ReadBlock(b.Index[k], b.Data[k], out b.Length[k]);
                    }
                    queue.Add(b, readerCts.Token);
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        try
        {
            foreach (var b in queue.GetConsumingEnumerable(ct))
            {
                Parallel.For(0, b.Count, k => Process(b, k, parentMap, req.Compression, level, key, header.ChainId));
                for (int k = 0; k < b.Count; k++)
                {
                    if (b.Ref[k] != null) writer.AddReference(b.Ref[k]!);
                    else
                    {
                        changed++;
                        writer.AddBlock(b.Index[k], b.Hash[k], b.Flags[k], b.Out[k], b.Out[k]?.Length ?? 0);
                    }
                    bytesRead += b.Length[k];
                    ArrayPool<byte>.Shared.Return(b.Data[k]);
                }
                tracker.Report(bytesRead);
            }
            readTask.GetAwaiter().GetResult();
        }
        catch
        {
            readerCts.Cancel();
            try { readTask.Wait(); } catch { }
            throw;
        }

        ct.ThrowIfCancellationRequested();
        writer.Commit();
        tracker.Report(bytesRead, force: true);
        long written = new FileInfo(path).Length;
        log.Info($"Image saved: {path} ({Util.FormatBytes(written)}; {changed:N0} of {blocks.Count:N0} blocks {(full ? "stored" : "changed")}; took {Util.FormatDuration(sw.Elapsed)}).");

        if (!req.DeferRetention)
            ImageCatalog.ApplyRetention(req.DestinationFolder, req.JobName, req.KeepFullSets, header.ChainId, log);

        return new BackupResult
        {
            ImagePath = path, ChainId = header.ChainId, WasFull = full, BytesRead = bytesRead, BytesWritten = written,
            Duration = sw.Elapsed, SpaceWarning = spaceWarning,
        };
    }

    /// <summary>Free bytes at a folder (local or \\server\share), or -1 if unknown. Replaceable for tests.</summary>
    public static Func<string, long> FreeSpaceProvider { get; set; } = folder =>
        Native.GetDiskFreeSpaceExW(folder.TrimEnd('\\') + "\\", out ulong avail, out _, out _) ? (long)avail : -1;

    /// <summary>
    /// Roughly how big the new image will be. Full: the job's previous full, scaled to how much data the
    /// disk holds now (or, for the very first full, the used data itself, a safe upper limit). Incremental:
    /// the largest recent incremental plus half again (or 10% of the full if there's none yet).
    /// </summary>
    public static long EstimateSize(bool full, long usedBytes, ImageChain? chain, IEnumerable<ImageEntry> jobImages)
    {
        if (full)
        {
            var prev = jobImages.Where(i => i.Header.IsFull && i.Header.UsedBytes > 0).OrderByDescending(i => i.Header.CreatedUtc).FirstOrDefault();
            if (prev == null) return usedBytes;
            return (long)(prev.FileSize * ((double)usedBytes / prev.Header.UsedBytes) * 1.1);
        }
        var incrementals = chain?.Images.Skip(1).ToList() ?? new List<ImageEntry>();
        long fullSize = chain?.Images[0].FileSize ?? usedBytes;
        long est = incrementals.Count > 0 ? (long)(incrementals.Max(i => i.FileSize) * 1.5) : fullSize / 10;
        return Math.Max(est, 64L << 20);
    }

    /// <summary>Stops before writing anything if the destination can't hold the new image; returns a warning when it's tight.</summary>
    private static string? CheckFreeSpace(BackupRequest req, bool full, long usedBytes, ImageChain? chain, Logger log)
    {
        long free = FreeSpaceProvider(req.DestinationFolder);
        if (free < 0)
        {
            log.Info("Couldn't read the free space at the destination; continuing.");
            return null;
        }
        var jobImages = ImageCatalog.Scan(req.DestinationFolder)
            .Where(i => string.Equals(i.Header.JobName, req.JobName, StringComparison.OrdinalIgnoreCase));
        long need = EstimateSize(full, usedBytes, chain, jobImages);
        string where = req.DestinationFolder;
        if (free < need)
            throw new IOException(
                $"Not enough space at {where}: this {(full ? "full" : "incremental")} backup needs about {Util.FormatBytes(need)}, " +
                $"but only {Util.FormatBytes(free)} is free. Free up space there, or lower \"Keep the last … full backups\". Nothing was written.");
        if (free < need * 1.2)
        {
            string w = $"Space is tight at {where}: about {Util.FormatBytes(need)} needed, {Util.FormatBytes(free)} free.";
            log.Warn(w);
            return w;
        }
        log.Info($"Destination has {Util.FormatBytes(free)} free; this backup needs about {Util.FormatBytes(need)}.");
        return null;
    }

    /// <summary>
    /// "Verify image when complete": re-reads what this backup wrote and checks every block's checksum.
    /// A failed check marks the image as damaged (so it's never built on) and throws.
    /// </summary>
    public static void VerifyNewImage(BackupResult result, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log, string? password = null)
    {
        log.Info($"Verifying image {Path.GetFileName(result.ImagePath)}…");
        int bad;
        using (var reader = new ImageReader(result.ImagePath))
        {
            if (reader.IsEncrypted) reader.Unlock(password ?? "");
            bad = reader.Verify(progress, ct, log, onlyThisFile: true);
        }
        if (bad > 0)
        {
            try { File.WriteAllText(ImageEntry.DamagedMarker(result.ImagePath), $"Failed verification on {DateTime.Now:g}: {bad} damaged block(s).\n"); }
            catch { }
            throw new InvalidDataException(
                $"The backup was saved, but verifying the image found {bad} damaged block(s). Don't rely on this backup; " +
                "the next run will start a fresh full backup. Check the destination drive or network.");
        }
        result.Verified = true;
        log.Info("Image verified: every block matches its checksum.");
    }

    private static void Process(BatchData b, int k, Dictionary<long, IndexEntry>? parentMap, CompressionChoice choice, CompressionLevel level,
        ImageKey? key, Guid chainId)
    {
        Store(b, k, parentMap, choice, level, key);
        // Encrypt after compressing (encrypted data doesn't compress).
        if (key != null && b.Ref[k] == null && b.Out[k] != null)
        {
            b.Out[k] = key.Seal(b.Out[k], b.Index[k], chainId);
            b.Flags[k] |= BlockFlags.Encrypted;
        }
    }

    private static void Store(BatchData b, int k, Dictionary<long, IndexEntry>? parentMap, CompressionChoice choice, CompressionLevel level, ImageKey? key)
    {
        var data = b.Data[k];
        int len = b.Length[k];
        var hash = key?.Hash(data, len) ?? ImageFormat.Hash(data, len);
        b.Hash[k] = hash;

        if (parentMap != null && parentMap.TryGetValue(b.Index[k], out var pe) && pe.Hash.AsSpan().SequenceEqual(hash))
        {
            b.Ref[k] = pe;
            return;
        }
        if (data.AsSpan(0, len).IndexOfAnyExcept((byte)0) < 0)
        {
            b.Flags[k] = BlockFlags.Zero;
            b.Out[k] = null;
            return;
        }
        if (choice != CompressionChoice.None)
        {
            using var ms = new MemoryStream(len / 2);
            using (var ds = new DeflateStream(ms, level, leaveOpen: true))
                ds.Write(data, 0, len);
            if (ms.Length < len - len / 50)
            {
                b.Flags[k] = BlockFlags.Deflate;
                b.Out[k] = ms.ToArray();
                return;
            }
        }
        b.Flags[k] = BlockFlags.Stored;
        b.Out[k] = data.AsSpan(0, len).ToArray();
    }

    private static void CleanupPartials(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*" + ImageFormat.Extension + ".partial"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f);
        }
        catch { }
    }
}
