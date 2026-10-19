namespace DiskImager.Core;

/// <summary>A folder whose files were left out of an image (path relative to its volume, e.g. \Users\amy\Downloads).</summary>
public sealed class ImageExclusion
{
    /// <summary>Start of the partition (in the source disk's layout) that holds the folder.</summary>
    public long PartitionOffset { get; set; }
    public string Folder { get; set; } = "";
}

/// <summary>
/// "Exclude folders": the files inside matching folders aren't stored in the image. After a restore the
/// folders still exist but are emptied, so no file is left pointing at data that was never backed up.
/// Patterns are full paths; * and ? match within one folder name, e.g. C:\Users\*\Downloads.
/// </summary>
public static class Exclusions
{
    public static readonly string[] Suggested =
    {
        @"C:\Users\*\Downloads",
        @"C:\Users\*\AppData\Local\Temp",
        @"C:\Windows\Temp",
        @"C:\$Recycle.Bin",
    };

    private static readonly string[] Protected =
        { "", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Windows\\System32", "Boot", "EFI" };

    /// <summary>Null if the pattern is usable, otherwise why not.</summary>
    public static string? Validate(string pattern)
    {
        string p = pattern.Trim().TrimEnd('\\');
        if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            return $"\"{pattern}\": use a full folder path like C:\\Users\\*\\Downloads.";
        string rel = p[3..];
        if (rel.Split('\\').Any(s => s is "" or "." or ".."))
            return $"\"{pattern}\": the path has an empty, . or .. part.";
        if (rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || rel.Contains(':'))
            return $"\"{pattern}\": the path contains characters that aren't allowed.";
        if (Protected.Any(x => string.Equals(rel, x, StringComparison.OrdinalIgnoreCase)) || rel == "*")
            return $"\"{pattern}\": excluding this would leave the restored PC unable to start. Pick a more specific folder.";
        return null;
    }

    /// <summary>Folders matching one pattern, looked up under <paramref name="root"/> (a drive like C: or a snapshot device).
    /// Returns paths relative to the root, starting with a backslash. Junctions and symbolic links are never followed.</summary>
    public static List<string> Expand(string root, string relativePattern)
    {
        var current = new List<string> { "" };
        foreach (var segment in relativePattern.Trim('\\').Split('\\'))
        {
            var next = new List<string>();
            foreach (var rel in current)
            {
                string dir = root + rel + "\\";
                if (segment.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    if (IsPlainDirectory(dir + segment)) next.Add(rel + "\\" + segment);
                    continue;
                }
                try
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var d in Directory.EnumerateDirectories(dir, segment, opts))
                        next.Add(rel + "\\" + Path.GetFileName(d));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            return a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>Every file below a folder, without following junctions or symbolic links.</summary>
    public static IEnumerable<string> FilesBelow(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });

    /// <summary>Deletes everything inside a folder but keeps the folder. Returns how many files were removed.</summary>
    public static int EmptyFolder(string folder, Logger? log)
    {
        if (!Directory.Exists(folder)) return 0;
        int files = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            try
            {
                if (entry is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    files += EmptyFolder(d.FullName, log);
                    d.Attributes = FileAttributes.Directory;
                    d.Delete(false);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete(); // a junction is removed without touching its target
                    if (entry is FileInfo) files++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Warn($"Couldn't remove {entry.FullName}: {ex.Message}");
            }
        }
        return files;
    }
}
