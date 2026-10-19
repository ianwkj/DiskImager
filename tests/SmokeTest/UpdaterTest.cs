using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskImager.Core;

/// <summary>Updater against a local fake of the GitHub releases API, signed with a throw-away test key.</summary>
static class UpdaterTest
{
    public static async Task Run(string work, Logger log, Action<bool, string> check)
    {
        // Version tags.
        check(Updater.ParseTag("v1.2.0") == new Version(1, 2, 0) && Updater.ParseTag("1.10") == new Version(1, 10, 0)
              && Updater.ParseTag("v1.2.0-beta") == null && Updater.ParseTag(null) == null,
            "update: version tags parsed (v1.2.0, 1.10; 'beta' tags ignored)");
        check(Updater.ParseTag("v1.10.0") > Updater.ParseTag("v1.9.9"), "update: 1.10.0 counts as newer than 1.9.9");

        // Signatures: made the way build-release.ps1 makes them (RSACryptoServiceProvider.SignHash over SHA-256).
        using var key = new RSACryptoServiceProvider(3072) { PersistKeyInCsp = false };
        string pub = key.ToXmlString(false);
        string Sign(byte[] data) => Convert.ToBase64String(key.SignHash(SHA256.HashData(data), CryptoConfig.MapNameToOID("SHA256")!));

        var setupBytes = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(setupBytes);
        string sig = Sign(setupBytes);
        string sample = Path.Combine(work, "sample-setup.exe");
        File.WriteAllBytes(sample, setupBytes);
        check(Updater.VerifySignature(sample, sig, pub), "update: signature from the release script is accepted");
        using (var other = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false })
            check(!Updater.VerifySignature(sample, sig, other.ToXmlString(false)), "update: signature from a different key is rejected");
        var tampered = (byte[])setupBytes.Clone();
        tampered[1000] ^= 1;
        File.WriteAllBytes(sample, tampered);
        check(!Updater.VerifySignature(sample, sig, pub), "update: a modified installer is rejected");
        check(!Updater.VerifySignature(sample, "not base64!", pub), "update: a garbage signature is rejected");
        check(Updater.VerifySignature(sample, Sign(tampered), UpdateKey.PublicKeyXml) == false, "update: the real DiskImager key rejects test-signed files");

        // Fake GitHub.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        string baseUrl = $"http://localhost:{port}";
        string mode = "ok";
        byte[] served = setupBytes;
        using var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();
        var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                string path = ctx.Request.Url!.AbsolutePath;
                byte[] body;
                int status = 200;
                if (path == $"/repos/{Updater.Repo}/releases/latest")
                {
                    if (mode == "none") { status = 404; body = Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"); }
                    else
                    {
                        var assets = new List<object> { new { name = "DiskImagerSetup-9.9.0.exe", size = served.Length, browser_download_url = baseUrl + "/dl/setup" } };
                        if (mode != "unsigned") assets.Add(new { name = "DiskImagerSetup-9.9.0.exe.sig", size = 512, browser_download_url = baseUrl + "/dl/sig" });
                        body = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            tag_name = "v9.9.0",
                            html_url = baseUrl + "/release",
                            body = "**Install:** run it.\r\n\r\n### In this version\r\n- Auto-update\r\n- `Faster` restores",
                            assets,
                        });
                    }
                }
                else if (path == "/dl/setup") body = served;
                else if (path == "/dl/sig") body = Encoding.ASCII.GetBytes(sig);
                else { status = 404; body = Array.Empty<byte>(); }
                ctx.Response.StatusCode = status;
                ctx.Response.ContentLength64 = body.Length;
                try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch { }
            }
        });

        string oldApi = Updater.ApiBase, oldKey = Updater.PublicKeyXml, oldFolder = Updater.UpdatesFolder;
        try
        {
            Updater.ApiBase = baseUrl;
            Updater.PublicKeyXml = pub;
            Updater.UpdatesFolder = Path.Combine(work, "Updates");
            Updater.CurrentOverride = new Version(1, 1, 9);

            var u = await Updater.CheckAsync(log);
            check(u != null && u.Version == new Version(9, 9, 0) && u.SetupName == "DiskImagerSetup-9.9.0.exe" && u.Size == setupBytes.Length,
                "update: newer signed release found on (fake) GitHub");
            check(u != null && Updater.ShortNotes(u.Notes) == "Install: run it.\nIn this version\n•  Auto-update\n•  Faster restores",
                "update: release notes shown without markdown symbols");

            Updater.CurrentOverride = new Version(9, 9, 0);
            check(await Updater.CheckAsync(log) == null, "update: nothing offered when already on the latest version");
            Updater.CurrentOverride = new Version(10, 0, 0);
            check(await Updater.CheckAsync(log) == null, "update: an older release on GitHub is never offered");
            Updater.CurrentOverride = new Version(1, 1, 9);

            mode = "unsigned";
            check(await Updater.CheckAsync(log) == null, "update: a release without a signature is ignored");
            mode = "none";
            check(await Updater.CheckAsync(log) == null, "update: no releases at all is not an error");
            mode = "ok";

            var reports = new List<double>();
            string path = await Updater.DownloadAsync(u!, new SyncProgress(reports.Add), log);
            check(File.Exists(path) && new FileInfo(path).Length == setupBytes.Length && Path.GetFileName(path) == "DiskImagerSetup-9.9.0.exe",
                "update: installer downloaded and verified");
            check(reports.Count > 0 && Math.Abs(reports[^1] - 1) < 1e-9, $"update: download progress reported ({reports.Count} updates, ends at 100%)");

            served = tampered;
            string? error = null;
            try { await Updater.DownloadAsync(u!, null, log); } catch (InvalidDataException ex) { error = ex.Message; }
            check(error != null && !Directory.EnumerateFiles(Updater.UpdatesFolder).Any(),
                "update: a tampered download is refused and deleted: " + error);

            Updater.ApiBase = "http://localhost:1"; // nothing listening
            bool threw = false;
            try { await Updater.CheckAsync(log); } catch (HttpRequestException) { threw = true; }
            check(threw, "update: being offline gives an error the window can ignore");
        }
        finally
        {
            Updater.ApiBase = oldApi; Updater.PublicKeyXml = oldKey; Updater.UpdatesFolder = oldFolder; Updater.CurrentOverride = null;
            cts.Cancel();
            listener.Stop();
            try { await server; } catch { }
        }
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this reports immediately so the test can count reliably.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
