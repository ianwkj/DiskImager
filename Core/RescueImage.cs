using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskImager.Core;

/// <summary>
/// The TechSource Rescue USB image (Linux desktop with DiskImager Rescue), published next to each DiskImager
/// release on GitHub: downloaded once into ProgramData\DiskImager\Rescue and written to a USB stick as-is.
/// </summary>
public static class RescueImage
{
    public sealed record Release(string Version, string Name, string Url, long Size, string? Sha256Url);

    public static string CacheFolder => Path.Combine(JobStore.DataFolder, "Rescue");

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DiskImager/{Updater.Current}");
        return http;
    }

    /// <summary>The newest release that has a rescue image attached, or null.</summary>
    public static async Task<Release?> FindLatest(CancellationToken ct = default)
    {
        using var http = Client(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Updater.ApiBase.TrimEnd('/')}/repos/{Updater.Repo}/releases?per_page=10");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (!rel.TryGetProperty("assets", out var assets)) continue;
            JsonElement? iso = null;
            string? sha = null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (Regex.IsMatch(name, @"^TechSource-Rescue-[\w.\-]+\.iso$", RegexOptions.IgnoreCase)) iso = a;
                if (name.EndsWith(".iso.sha256", StringComparison.OrdinalIgnoreCase)) sha = a.GetProperty("browser_download_url").GetString();
            }
            if (iso is JsonElement i)
                return new Release(rel.GetProperty("tag_name").GetString() ?? "", i.GetProperty("name").GetString()!,
                    i.GetProperty("browser_download_url").GetString()!, i.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// <summary>Downloads the image (or reuses an earlier complete download) and returns its path.</summary>
    public static async Task<string> Download(Release r, IProgress<ProgressInfo>? progress, Logger log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheFolder);
        string path = Path.Combine(CacheFolder, r.Name);
        if (File.Exists(path) && new FileInfo(path).Length == r.Size)
        {
            log.Info($"Rescue image {r.Name} already downloaded.");
            return path;
        }
        foreach (var old in Directory.EnumerateFiles(CacheFolder, "*.iso*")) try { File.Delete(old); } catch { }

        using var http = Client(TimeSpan.FromHours(3));
        string partial = path + ".partial";
        using (var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var tracker = new ProgressTracker(progress, "Downloading the rescue system", r.Size);
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buf = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                tracker.Report(done);
            }
        }
        if (new FileInfo(partial).Length != r.Size) throw new IOException("The download was incomplete. Try again.");
        if (r.Sha256Url != null)
        {
            progress?.Report(new ProgressInfo("Checking the download", 0, 0, 0));
            string expected = (await http.GetStringAsync(r.Sha256Url, ct)).Trim().Split(' ', '\t')[0];
            string actual;
            await using (var f = File.OpenRead(partial)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("The downloaded rescue system is damaged (checksum mismatch). Try again.");
            }
        }
        File.Move(partial, path, overwrite: true);
        log.Info($"Downloaded the rescue system {r.Name} ({Util.FormatBytes(r.Size)}).");
        return path;
    }

    /// <summary>Writes the image byte-for-byte onto the USB stick (the stick's previous contents are erased).</summary>
    public static void WriteToUsb(string iso, DiskInfo usb, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        if (usb.IsSystem) throw new InvalidOperationException("That's the disk Windows is running from.");
        long size = new FileInfo(iso).Length;
        if (usb.Size < size) throw new InvalidOperationException($"The USB stick is too small: the rescue system needs {Util.FormatBytes(size)} (use a 16 GB stick for saved storage).");
        using var src = new FileBlockSource(iso, usb.SectorSize);
        DiskWriterEngine.Write(src, usb, newIds: false, "Writing the rescue stick", progress, ct, log);
        log.Info($"Rescue stick written to {usb.Title}.");
    }

    /// <summary>A plain file read as disk blocks (every block counts as used).</summary>
    private sealed class FileBlockSource : IBlockSource
    {
        private readonly FileStream _fs;
        private readonly long[] _blocks;

        public FileBlockSource(string path, int sectorSize)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            DiskSize = _fs.Length;
            SectorSize = sectorSize;
            _blocks = Enumerable.Range(0, (int)((DiskSize + BlockSize - 1) / BlockSize)).Select(i => (long)i).ToArray();
        }

        public int BlockSize => ImageFormat.DefaultBlockSize;
        public long DiskSize { get; }
        public int SectorSize { get; }
        public DiskStyle Style => DiskStyle.RAW;
        public long MaxPartitionEnd => DiskSize;
        public IReadOnlyList<long> Blocks => _blocks;

        public void ReadBlock(long blockIndex, byte[] buffer, out int length)
        {
            long off = blockIndex * BlockSize;
            length = (int)Math.Min(BlockSize, DiskSize - off);
            _fs.Position = off;
            _fs.ReadExactly(buffer, 0, length);
        }

        public void Dispose() => _fs.Dispose();
    }
}
