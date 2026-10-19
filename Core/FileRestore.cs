using DiskImager.Core.Ntfs;

namespace DiskImager.Core;

public sealed class FileCopyResult
{
    public int Files { get; set; }
    public int Folders { get; set; }
    public long Bytes { get; set; }
    /// <summary>Items that couldn't be copied, with the reason.</summary>
    public List<string> Problems { get; } = new();
}

/// <summary>
/// File-level restore: copies files and folders out of a backup image (read through <see cref="NtfsVolume"/>)
/// into an ordinary folder, keeping their dates and read-only/hidden flags.
/// </summary>
public static class FileRestore
{
    /// <summary>Opens the NTFS partitions of an image: (partition, volume) for each one that can be browsed.</summary>
    public static List<(ImagePartition Partition, NtfsVolume Volume)> OpenVolumes(ImageReader image)
    {
        var list = new List<(ImagePartition, NtfsVolume)>();
        foreach (var p in image.Header.Partitions.Where(p => !p.IsContainer).OrderBy(p => p.Offset))
        {
            try
            {
                var reader = new ImageVolumeReader(image, p.Offset, p.Length);
                if (NtfsVolume.IsNtfs(reader)) list.Add((p, new NtfsVolume(reader)));
            }
            catch { /* not NTFS, or unreadable: not browsable */ }
        }
        return list;
    }

    public static string Describe(ImagePartition p) =>
        string.Join("  ", new[] { p.DriveLetter, p.Label.Length > 0 ? p.Label : p.Type, Util.FormatBytes(p.Length) }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>True if the path lies in a folder that was excluded from the backup (its files' data isn't in the image).</summary>
    public static bool IsExcluded(string path, IEnumerable<string> excludedFolders) =>
        excludedFolders.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                                 path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    public static FileCopyResult Copy(NtfsVolume vol, IEnumerable<NtfsEntry> items, string destination, IReadOnlyList<string> excludedFolders,
        IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        var result = new FileCopyResult();
        Directory.CreateDirectory(destination);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;
        void Report(string what)
        {
            if (progress == null || sw.ElapsedMilliseconds - lastReport < 200) return;
            lastReport = sw.ElapsedMilliseconds;
            progress.Report(new ProgressInfo($"Copying {what}", result.Bytes, 0, result.Bytes / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
        }

        void CopyEntry(NtfsEntry e, string targetFolder)
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(targetFolder, SafeName(e.Name));
            if (e.IsLink)
            {
                result.Problems.Add($"{e.Path}: a link to another location, not copied");
                return;
            }
            if (IsExcluded(e.Path, excludedFolders) && !e.IsDirectory)
            {
                result.Problems.Add($"{e.Path}: in a folder excluded from this backup, so its contents weren't saved");
                return;
            }
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
                result.Folders++;
                List<NtfsEntry> children;
                try { children = vol.ListDirectory(e.Record, e.Path); }
                catch (Exception ex) { result.Problems.Add($"{e.Path}: folder couldn't be read ({ex.Message})"); return; }
                foreach (var c in children) CopyEntry(c, target);
                vol.Refresh(e);
                TrySetTimes(target, e, isDir: true);
                return;
            }

            Report(e.Path);
            try
            {
                vol.Refresh(e);
                using (var input = vol.OpenFile(e.Record))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 20];
                    long remaining = input.Length;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = input.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                        if (n <= 0) throw new InvalidDataException("unexpected end of data");
                        output.Write(buf, 0, n);
                        remaining -= n;
                        result.Bytes += n;
                        Report(e.Path);
                    }
                }
                TrySetTimes(target, e, isDir: false);
                result.Files++;
            }
            catch (OperationCanceledException) { TryDelete(target); throw; }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or UnauthorizedAccessException or ImageLockedException)
            {
                TryDelete(target);
                result.Problems.Add($"{e.Path}: {ex.Message}");
            }
        }

        foreach (var item in items) CopyEntry(item, destination);
        progress?.Report(new ProgressInfo("Copy finished", result.Bytes, result.Bytes, 0));
        log.Info($"File restore: copied {result.Files:N0} files in {result.Folders:N0} folders ({Util.FormatBytes(result.Bytes)}) to {destination}" +
                 (result.Problems.Count > 0 ? $"; {result.Problems.Count} item(s) couldn't be copied." : "."));
        foreach (var p in result.Problems.Take(50)) log.Warn("File restore: " + p);
        return result;
    }

    /// <summary>NTFS allows a few names Windows programs can't create (trailing dots/spaces, reserved names).</summary>
    private static string SafeName(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "_";
        string stem = Path.GetFileNameWithoutExtension(clean).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            clean = "_" + clean;
        return clean;
    }

    private static void TrySetTimes(string path, NtfsEntry e, bool isDir)
    {
        try
        {
            if (e.ModifiedUtc > DateTime.MinValue)
            {
                if (isDir) Directory.SetLastWriteTimeUtc(path, e.ModifiedUtc);
                else File.SetLastWriteTimeUtc(path, e.ModifiedUtc);
            }
            if (!isDir)
            {
                var keep = e.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
                if (keep != 0) File.SetAttributes(path, keep);
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { }
    }
}
