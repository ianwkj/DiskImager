using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using DiskImager.Core.Ntfs;
using DiskImager.Rescue.Pages;

namespace DiskImager.Rescue;

/// <summary>
/// Self-test in demo mode (diskimager-rescue --demo DIR --autotest SHOTS [--manifest file.json] [--password pw]):
/// walks every screen, saves screenshots, restores the newest backup onto the last demo disk, checks the
/// restored NTFS files against the manifest, then browses the backup. Writes SHOTS\results.txt.
/// </summary>
public static class DemoAutoTest
{
    public static async Task<int> Run(MainWindow w, string shots, string? manifest, string password)
    {
        Directory.CreateDirectory(shots);
        var lines = new List<string>();
        int failed = 0;
        void Check(bool ok, string what) { lines.Add($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failed++; }
        async Task Shot(string name)
        {
            await Task.Delay(800);
            var size = new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(w);
            rtb.Save(Path.Combine(shots, $"rescue-{name}.png"));
        }
        T Page<T>() where T : class => (w.Host.Content as T) ?? throw new InvalidOperationException($"Expected the {typeof(T).Name} page, found {w.Host.Content?.GetType().Name}.");

        try
        {
            await Shot("1-start");

            // Restore flow.
            w.State.BrowseMode = false;
            var find = new FindBackupPage(w);
            w.Show(find, 1);
            await find.LoadTask;
            await Shot("2-find-backup");
            find.PickNewest(password);
            await Shot("3-backup-picked");
            await find.NextAsync();
            var choose = Page<ChooseDiskPage>();
            await choose.LoadTask;
            choose.PickLast();
            await Shot("4-choose-disk");
            choose.Next();
            var confirm = Page<ConfirmPage>();
            Check(confirm.Problem == null, "confirm page: the plan is valid " + confirm.Problem);
            await Shot("5-confirm");
            confirm.Confirm();
            var restore = Page<RestorePage>();
            await Task.Delay(400);
            await Shot("6-restoring");
            await restore.Finished;
            await Shot("7-restore-done");
            Check(restore.Succeeded, "restore finished");

            if (manifest != null && w.State.Target != null) Check(VerifyRestored(w.State.Target.Path, manifest, out string detail), "restored disk: NTFS files identical to the original " + detail);

            // Browse flow.
            w.GoHome();
            w.State.BrowseMode = true;
            find = new FindBackupPage(w);
            w.Show(find, 1);
            await find.LoadTask;
            find.PickNewest(password);
            await find.NextAsync();
            var browse = Page<BrowsePage>();
            await browse.Opened;
            await Task.Delay(1200);
            Check(browse.ItemCount > 0, $"browse: the backup's root folder lists {browse.ItemCount} items");
            await Shot("8-browse");

            w.Show(new HealthPage(w));
            await Shot("9-health");
            w.Show(new ToolsPage(w));
            await Shot("10-tools");
        }
        catch (Exception ex)
        {
            Check(false, "self-test stopped: " + ex);
        }
        lines.Add(failed == 0 ? "RESCUE APP TESTS PASSED" : $"{failed} RESCUE APP TEST(S) FAILED");
        File.WriteAllLines(Path.Combine(shots, "results.txt"), lines);
        return failed == 0 ? 0 : 1;
    }

    private sealed record ManifestEntry(string Path, long Size, string Sha256);

    /// <summary>Opens the NTFS partition(s) of the restored GPT disk file and compares every listed file.</summary>
    private static bool VerifyRestored(string diskPath, string manifestFile, out string detail)
    {
        var manifest = JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(manifestFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var fs = File.OpenRead(diskPath);
        var gpt = new byte[512 * 34];
        fs.ReadExactly(gpt);
        for (int i = 0; i < 128; i++)
        {
            int e = 1024 + i * 128;
            long first = BitConverter.ToInt64(gpt, e + 32), last = BitConverter.ToInt64(gpt, e + 40);
            if (first == 0) continue;
            var reader = new StreamVolumeReader(new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), first * 512, (last - first + 1) * 512);
            if (!NtfsVolume.IsNtfs(reader)) continue;
            var vol = new NtfsVolume(reader);
            int ok = 0, total = 0;
            foreach (var m in manifest.Where(m => !m.Path.EndsWith("lzx.txt")))
            {
                total++;
                var entry = vol.Resolve(m.Path);
                if (entry == null) continue;
                using var s = vol.OpenFile(entry.Record);
                if (Convert.ToHexString(SHA256.HashData(s)) == m.Sha256) ok++;
            }
            detail = $"({ok} of {total} files, partition grown to {Util(last - first + 1)})";
            return ok == total;
        }
        detail = "(no NTFS partition found on the restored disk)";
        return false;
        static string Util(long sectors) => Core.Util.FormatBytes(sectors * 512);
    }
}
