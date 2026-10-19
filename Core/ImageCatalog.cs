namespace DiskImager.Core;

public sealed class ImageEntry
{
    public string Path { get; init; } = "";
    public ImageHeader Header { get; init; } = new();
    public long FileSize { get; init; }
    /// <summary>Failed "verify image when complete"; never used as a parent and shown as damaged.</summary>
    public bool Damaged => File.Exists(DamagedMarker(Path));

    public static string DamagedMarker(string imagePath) => imagePath + ".damaged";
}

/// <summary>A backup set: one full image followed by its incrementals.</summary>
public sealed class ImageChain
{
    public Guid ChainId { get; init; }
    public List<ImageEntry> Images { get; init; } = new();
    public bool IsComplete => Images.Count > 0 && Images.Select((img, i) => img.Header.ChainOrdinal == i).All(x => x);
    public DateTime StartedUtc => Images.Min(i => i.Header.CreatedUtc);
}

public static class ImageCatalog
{
    public static List<ImageEntry> Scan(string folder, int depth = 0)
    {
        var result = new List<ImageEntry>();
        if (!Directory.Exists(folder)) return result;
        var opts = new EnumerationOptions { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(folder, "*" + ImageFormat.Extension, opts))
        {
            try
            {
                result.Add(new ImageEntry { Path = f, Header = ImageFormat.ReadHeader(f), FileSize = new FileInfo(f).Length });
            }
            catch
            {
                // Not ours, or damaged: ignore.
            }
        }
        return result;
    }

    public static List<ImageChain> Chains(IEnumerable<ImageEntry> entries) =>
        entries.GroupBy(e => (System.IO.Path.GetDirectoryName(e.Path), e.Header.ChainId))
            .Select(g => new ImageChain { ChainId = g.Key.ChainId, Images = g.OrderBy(e => e.Header.ChainOrdinal).ToList() })
            .OrderByDescending(c => c.StartedUtc)
            .ToList();

    public static List<ImageChain> ChainsForJob(string folder, string jobName) =>
        Chains(Scan(folder).Where(e => string.Equals(e.Header.JobName, jobName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Deletes the oldest backup sets of a job, keeping the newest <paramref name="keep"/> good ones.
    /// Sets containing an image that failed verification don't count towards the number kept
    /// (so a bad new backup can never cause a good old one to be deleted).
    /// </summary>
    public static void ApplyRetention(string folder, string jobName, int keep, Guid protectChain, Logger log)
    {
        if (keep <= 0) return;
        var chains = ChainsForJob(folder, jobName); // newest first
        int good = 0, cut = chains.Count;
        for (int i = 0; i < chains.Count; i++)
        {
            if (!chains[i].Images.Any(img => img.Damaged)) good++;
            if (good == keep) { cut = i + 1; break; }
        }
        foreach (var chain in chains.Skip(cut))
        {
            if (chain.ChainId == protectChain) continue;
            foreach (var img in chain.Images)
            {
                try
                {
                    File.Delete(img.Path);
                    File.Delete(ImageEntry.DamagedMarker(img.Path));
                    log.Info($"Retention: deleted old image {System.IO.Path.GetFileName(img.Path)}.");
                }
                catch (Exception ex)
                {
                    log.Warn($"Retention: could not delete {img.Path}: {ex.Message}");
                }
            }
        }
    }
}
