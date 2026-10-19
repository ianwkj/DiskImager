using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskImager.Core;

/// <summary>Unattended rescue restore for testing: rescue-autotest.json on any drive's root.</summary>
public sealed class RescueAutotest
{
    /// <summary>Folder (path without drive letter) holding the images, searched on every drive.</summary>
    public string ImageFolder { get; set; } = "";
    /// <summary>Restore onto the disk of exactly this size.</summary>
    public long TargetSize { get; set; }
    public string? Password { get; set; }
    public string Layout { get; set; } = "auto";
    /// <summary>Volume label of the drive that receives rescue-result.json and the log.</summary>
    public string ResultLabel { get; set; } = "RESULTS";
    public bool PowerOff { get; set; } = true;
}

/// <summary>
/// Rescue mode (DiskImager.exe --rescue), started by the rescue USB in Windows PE: find a backup on a USB drive
/// or the NAS, pick the disk to restore it onto, restore. A text menu, so it works in the bare rescue system.
/// </summary>
public static class Rescue
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    private static readonly Logger Log = new();

    public static int Run()
    {
        AllocConsole();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { Console.Title = "DiskImager Rescue"; } catch { }

        var auto = FindAutotest(out string? autotestPath);
        if (auto != null) return RunAutotest(auto, autotestPath!);

        while (true)
        {
            Header();
            Console.WriteLine("  1  Restore a backup onto a disk");
            Console.WriteLine("  2  Connect to a network share (NAS)");
            Console.WriteLine("  3  Show disks and drives");
            Console.WriteLine("  4  Open a command prompt");
            Console.WriteLine("  5  Restart");
            Console.WriteLine("  6  Shut down");
            switch (Ask("\nChoose 1-6"))
            {
                case "1": Guard(RestoreWizard); break;
                case "2": Guard(ConnectShare); break;
                case "3": Guard(ShowDisks); break;
                case "4": Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true }); break;
                case "5": Power("reboot"); return 0;
                case "6": Power("shutdown"); return 0;
            }
        }
    }

    private static void Header()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  DiskImager Rescue");
        Console.ResetColor();
        Console.WriteLine("  Powered by TechSource Digital · e-techsource.com");
        var ips = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()).ToList();
        Console.WriteLine(ips.Count > 0 ? $"  Network: connected ({string.Join(", ", ips)})" : "  Network: not connected (no network cable, or no driver for this network card)");
        Console.WriteLine(new string('-', 70));
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt + ": ");
        return (Console.ReadLine() ?? "").Trim();
    }

    private static string AskHidden(string prompt)
    {
        Console.Write(prompt + ": ");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n  " + ex.Message);
            Console.ResetColor();
            Log.Error("Rescue: " + ex);
        }
        Ask("\nPress Enter to go back to the menu");
    }

    private static void ShowDisks()
    {
        Console.WriteLine();
        foreach (var d in DiskEnumerator.GetDisks())
            Console.WriteLine($"  Disk {d.Number}: {d.Model} ({Util.FormatBytes(d.Size)})  {string.Join("  ", d.Partitions.Where(p => p.Volume?.DriveLetter != null).Select(p => $"{p.Volume!.DriveLetter} {p.Volume.Label}"))}");
        Console.WriteLine("\n  Drives:");
        foreach (var v in DiskEnumerator.GetVolumes().Where(v => v.DriveLetter != null))
            Console.WriteLine($"  {v.DriveLetter}  {v.Label,-20} {v.FileSystem,-6} {Util.FormatBytes(v.Length)}");
    }

    private static void ConnectShare()
    {
        string share = Ask("\nNetwork folder, e.g. \\\\NAS\\Backups");
        if (!NetworkShare.IsNetworkPath(share)) throw new ArgumentException("Enter a path starting with \\\\.");
        string user = Ask("User name (e.g. NAS\\backupuser; leave empty for none)");
        string pass = user.Length > 0 ? AskHidden("Password") : "";
        NetworkShare.Connect(share, user.Length > 0 ? user : null, pass, Log);
        Console.WriteLine(Directory.Exists(share) ? "\n  Connected." : "\n  Connected, but that folder couldn't be opened.");
    }

    private static void RestoreWizard()
    {
        Console.WriteLine("\n  Where are the backups? Type a folder (E:\\Backups or \\\\NAS\\Backups),");
        Console.WriteLine("  or press Enter to search every attached drive.");
        string where = Ask("Folder");
        List<ImageEntry> images;
        if (where.Length == 0)
        {
            images = new();
            foreach (var v in DiskEnumerator.GetVolumes().Where(v => v.DriveLetter != null && v.DriveLetter != "X:"))
                images.AddRange(ImageCatalog.Scan(v.DriveLetter + "\\", depth: 4));
        }
        else
        {
            if (NetworkShare.IsNetworkPath(where) && !Directory.Exists(where))
            {
                string user = Ask("User name for the share (leave empty for none)");
                if (user.Length > 0) NetworkShare.Connect(where, user, AskHidden("Password"), Log);
            }
            images = ImageCatalog.Scan(where, depth: 3);
        }
        var list = images.OrderByDescending(i => i.Header.CreatedUtc).Take(30).ToList();
        if (list.Count == 0) throw new InvalidOperationException("No DiskImager backups found there.");

        Console.WriteLine();
        for (int i = 0; i < list.Count; i++)
        {
            var h = list[i].Header;
            Console.WriteLine($"  {i + 1,2}  {h.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {h.KindText,-15} {h.ComputerName,-16} {h.JobName}{(h.IsEncrypted ? "  [encrypted]" : "")}{(list[i].Damaged ? "  [DAMAGED]" : "")}");
        }
        if (!int.TryParse(Ask($"\nWhich backup (1-{list.Count})"), out int pick) || pick < 1 || pick > list.Count) return;
        var img = list[pick - 1];

        using var reader = new ImageReader(img.Path);
        var missing = reader.MissingChainFiles();
        if (missing.Count > 0) throw new InvalidOperationException(missing[0]);
        while (reader.IsEncrypted)
        {
            try { reader.Unlock(AskHidden("This backup is encrypted. Password")); }
            catch (WrongPasswordException ex) { Console.WriteLine("  " + ex.Message); }
        }

        int? imageDisk = DiskEnumerator.DiskNumberOfPath(img.Path);
        var disks = DiskEnumerator.GetDisks().Where(d => d.Number != imageDisk).ToList();
        Console.WriteLine($"\n  Restore onto which disk? EVERYTHING on it will be replaced. (The backup needs at least {Util.FormatBytes(reader.MaxPartitionEnd)} unless resized.)");
        foreach (var d in disks)
            Console.WriteLine($"  Disk {d.Number}: {d.Model} ({Util.FormatBytes(d.Size)})  {string.Join(" ", d.Partitions.Where(p => p.Volume?.DriveLetter != null).Select(p => p.Volume!.DriveLetter + " " + p.Volume.Label))}");
        if (!int.TryParse(Ask("Disk number"), out int n)) return;
        var target = disks.FirstOrDefault(d => d.Number == n) ?? throw new ArgumentException("No such disk.");

        var plan = Layout.Original(SourceDescription.FromImage(reader), target.Size, target.SectorSize);
        Layout.AutoFit(plan);
        if (plan.Validate() is string problem) throw new InvalidOperationException(problem);
        if (plan.NeedsShrink)
            throw new InvalidOperationException("This disk is smaller than the backup's partitions, which needs shrinking. Use DiskImager in Windows for that, or choose a bigger disk.");

        Console.WriteLine($"\n  About to restore the {img.Header.KindText.ToLowerInvariant()} from {img.Header.CreatedUtc.ToLocalTime():g} ({img.Header.ComputerName})");
        Console.WriteLine($"  onto Disk {target.Number}: {target.Model} ({Util.FormatBytes(target.Size)}).");
        foreach (var p in plan.Parts.Where(p => p.TargetLength != p.Source.Length))
            Console.WriteLine($"  {p.Source.Name}: {Util.FormatBytes(p.Source.Length)} -> {Util.FormatBytes(p.TargetLength)}");
        if (!string.Equals(Ask("\nType ERASE to continue"), "ERASE", StringComparison.Ordinal)) { Console.WriteLine("  Cancelled. Nothing was changed."); return; }

        var sw = Stopwatch.StartNew();
        DiskCopy.Execute(reader, plan, target, newIds: false, null, "Restoring", ConsoleProgress(), CancellationToken.None, Log);
        Console.WriteLine($"\n\n  Restore complete in {Util.FormatDuration(sw.Elapsed)}. Remove the rescue USB stick, then choose Restart.");
    }

    private static IProgress<ProgressInfo> ConsoleProgress()
    {
        string last = "";
        return new SyncProgress(p =>
        {
            string line = p.Total > 0
                ? $"  {p.Stage}: {100.0 * p.Done / p.Total,5:0.0}%  {Util.FormatBytes(p.Done)} of {Util.FormatBytes(p.Total)}  {Util.FormatBytes(p.BytesPerSecond)}/s"
                : $"  {p.Stage}…";
            if (line == last) return;
            last = line;
            Console.Write("\r" + line.PadRight(Math.Max(0, Console.WindowWidth - 1))[..Math.Max(1, Math.Min(line.Length + 10, Console.WindowWidth - 1))]);
        });
    }

    private sealed class SyncProgress(Action<ProgressInfo> a) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => a(value);
    }

    private static void Power(string what)
    {
        try { Process.Start(new ProcessStartInfo("wpeutil.exe", what) { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); }
        catch { Process.Start(new ProcessStartInfo("shutdown.exe", what == "reboot" ? "/r /t 0" : "/s /t 0") { UseShellExecute = false }); }
    }

    // ------------------------------------------------------------------ unattended (tests)

    private static RescueAutotest? FindAutotest(out string? path)
    {
        path = null;
        foreach (var v in DiskEnumerator.GetVolumes().Where(v => v.DriveLetter != null))
        {
            string f = v.DriveLetter + "\\rescue-autotest.json";
            if (!File.Exists(f)) continue;
            path = f;
            try { return JsonSerializer.Deserialize<RescueAutotest>(File.ReadAllText(f)); }
            catch { return null; }
        }
        return null;
    }

    private static int RunAutotest(RescueAutotest t, string configPath)
    {
        var sw = Stopwatch.StartNew();
        string result;
        bool ok = false;
        try
        {
            Console.WriteLine($"DiskImager Rescue: unattended restore ({configPath})");
            var vols = DiskEnumerator.GetVolumes().Where(v => v.DriveLetter != null).ToList();
            var image = vols.Select(v => v.DriveLetter + "\\" + t.ImageFolder.Trim('\\'))
                .Where(Directory.Exists)
                .SelectMany(f => ImageCatalog.Scan(f))
                .OrderByDescending(i => i.Header.CreatedUtc).FirstOrDefault()
                ?? throw new InvalidOperationException($"No image found in \\{t.ImageFolder} on any drive.");
            using var reader = new ImageReader(image.Path);
            if (reader.IsEncrypted) reader.Unlock(t.Password ?? "");
            var target = DiskEnumerator.GetDisks().FirstOrDefault(d => d.Size == t.TargetSize && d.Number != DiskEnumerator.DiskNumberOfPath(image.Path))
                ?? throw new InvalidOperationException($"No disk of exactly {t.TargetSize} bytes found.");
            var plan = Layout.Original(SourceDescription.FromImage(reader), target.Size, target.SectorSize);
            if (t.Layout == "auto") Layout.AutoFit(plan);
            Console.WriteLine($"Restoring {Path.GetFileName(image.Path)} onto Disk {target.Number} ({Util.FormatBytes(target.Size)})");
            DiskCopy.Execute(reader, plan, target, newIds: false, null, "Restoring", ConsoleProgress(), CancellationToken.None, Log);
            ok = true;
            result = $"Restored {Path.GetFileName(image.Path)} onto Disk {target.Number} in {sw.Elapsed.TotalSeconds:0} s";
        }
        catch (Exception ex)
        {
            result = "FAILED: " + ex.Message;
            Log.Error("Rescue autotest: " + ex);
        }
        Console.WriteLine("\n" + result);

        var results = DiskEnumerator.GetVolumes().FirstOrDefault(v => v.DriveLetter != null && string.Equals(v.Label, t.ResultLabel, StringComparison.OrdinalIgnoreCase));
        if (results != null)
        {
            try
            {
                File.WriteAllText(results.DriveLetter + "\\rescue-result.json", JsonSerializer.Serialize(new { Ok = ok, Message = result, Seconds = sw.Elapsed.TotalSeconds, Version = typeof(Rescue).Assembly.GetName().Version?.ToString() }));
                if (File.Exists(Log.FilePath)) File.Copy(Log.FilePath, results.DriveLetter + "\\rescue-log.txt", overwrite: true);
            }
            catch (Exception ex) { Console.WriteLine("Couldn't save the result: " + ex.Message); }
        }
        if (t.PowerOff) Power("shutdown");
        return ok ? 0 : 1;
    }
}
