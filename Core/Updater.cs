using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskImager.Core;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string SetupName, string SetupUrl, string SignatureUrl, long Size, string Notes, string PageUrl);

/// <summary>
/// Updates from the GitHub releases of ianwkj/DiskImager. A release is only used if it has both
/// DiskImagerSetup-x.y.z.exe and DiskImagerSetup-x.y.z.exe.sig, and the signature matches <see cref="UpdateKey"/>.
/// The installer then runs silently, waits for this window to close, and reopens DiskImager.
/// </summary>
public static class Updater
{
    public const string Repo = "ianwkj/DiskImager";

    // Replaceable for tests (a local fake GitHub and a test key).
    public static string ApiBase { get; set; } = Environment.GetEnvironmentVariable("DISKIMAGER_UPDATE_API") ?? "https://api.github.com";
    public static string PublicKeyXml { get; set; } = UpdateKey.PublicKeyXml;
    public static Version? CurrentOverride { get; set; }

    public static Version Current
    {
        get
        {
            if (CurrentOverride != null) return CurrentOverride;
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string UpdatesFolder { get; set; } = Path.Combine(JobStore.DataFolder, "Updates");

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DiskImager/{Current}");
        return http;
    }

    /// <summary>"v1.2.0" or "1.2.0" → 1.2.0; null if it isn't a version.</summary>
    public static Version? ParseTag(string? tag)
    {
        var m = Regex.Match(tag ?? "", @"^v?(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>The latest release if it is newer than this copy and properly signed; otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Logger? log, CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase.TrimEnd('/')}/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var version = ParseTag(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
        if (version == null || version <= Current) return null;

        string? setupName = null, setupUrl = null, sigUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (Regex.IsMatch(name, @"^DiskImagerSetup-[\d.]+\.exe$", RegexOptions.IgnoreCase))
                {
                    setupName = name; setupUrl = url;
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
            }
            if (setupName != null)
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), setupName + ".sig", StringComparison.OrdinalIgnoreCase))
                        sigUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (setupUrl == null || sigUrl == null)
        {
            log?.Warn($"Update {version} is on GitHub but has no signed installer; ignoring it.");
            return null;
        }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new UpdateInfo(version, setupName!, setupUrl, sigUrl, size, notes, page);
    }

    /// <summary>Downloads the installer and checks its signature. Returns the local path; throws if anything is wrong.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, Logger? log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesFolder);
        foreach (var old in Directory.EnumerateFiles(UpdatesFolder))
            try { File.Delete(old); } catch { }

        string path = Path.Combine(UpdatesFolder, u.SetupName);
        string partial = path + ".partial";
        using var http = NewClient(TimeSpan.FromMinutes(30));

        string signature = (await http.GetStringAsync(u.SignatureUrl, ct)).Trim();

        using (var resp = await http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(partial))
            {
                var buf = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }
        }

        if (!VerifySignature(partial, signature, PublicKeyXml))
        {
            TryDelete(partial);
            throw new InvalidDataException("The downloaded update isn't signed by TechSource Digital, so it wasn't installed.");
        }
        File.Move(partial, path, overwrite: true);
        log?.Info($"Downloaded and verified {u.SetupName} ({Util.FormatBytes(new FileInfo(path).Length)}).");
        return path;
    }

    /// <summary>RSA (PKCS#1 v1.5) signature over the SHA-256 of the file, base64.</summary>
    public static bool VerifySignature(string file, string signatureBase64, string publicKeyXml)
    {
        try
        {
            byte[] sig = Convert.FromBase64String(signatureBase64.Trim());
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKeyXml);
            byte[] hash;
            using (var f = File.OpenRead(file)) hash = SHA256.HashData(f);
            return rsa.VerifyHash(hash, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts the installer: it waits for this process to exit, installs silently and reopens DiskImager.</summary>
    public static void StartInstall(string setupPath, Logger? log)
    {
        try
        {
            var s = AppSettings.Load();
            s.Updates.LastAttemptVersion = ParseTag(Regex.Match(Path.GetFileNameWithoutExtension(setupPath), @"[\d.]+$").Value)?.ToString();
            s.Updates.LastAttemptUtc = DateTime.UtcNow;
            s.Save();
        }
        catch (Exception ex) { log?.Warn("Couldn't record the update attempt: " + ex.Message); }
        log?.Info($"Installing {Path.GetFileName(setupPath)}; DiskImager will close and reopen.");
        Process.Start(new ProcessStartInfo(setupPath, $"/silent /waitpid {Environment.ProcessId} /relaunch")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>True if the last update attempt (within a day) didn't result in a newer version: don't auto-install it again.</summary>
    public static bool LastAttemptFailed(Version candidate)
    {
        var u = AppSettings.Load().Updates;
        return u.LastAttemptVersion != null && Version.TryParse(u.LastAttemptVersion, out var v) && v == candidate
            && Current < v && DateTime.UtcNow - u.LastAttemptUtc < TimeSpan.FromDays(1);
    }

    /// <summary>The first few lines of the release notes, without markdown symbols.</summary>
    public static string ShortNotes(string notes, int maxLines = 8)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l, @"\*\*|`|^#+\s*", "").Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l)
            .Take(maxLines);
        return string.Join("\n", lines);
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }
}
