// Installer test in a sandbox: dotnet run -- --setup <DiskImagerSetup.exe> <sandbox folder> <screenshot folder>
// Runs setup with /sandbox (temp folder + a test registry key under HKCU), clicks Install through UI
// Automation, and checks the program file, shortcuts and Installed-apps entry it created.
using System.Diagnostics;
using System.Windows.Automation;
using Microsoft.Win32;

static class SetupTest
{
    const string TestKey = @"Software\TechSource Digital\DiskImagerSetupTest\Uninstall\DiskImager";

    public static int Run(string setupExe, string sandbox, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\TechSource Digital\DiskImagerSetupTest", false);
        Directory.CreateDirectory(shots);

        // The version the setup file says it installs, e.g. "1.1.8".
        var sv = FileVersionInfo.GetVersionInfo(setupExe);
        string version = $"{sv.FileMajorPart}.{sv.FileMinorPart}.{sv.FileBuildPart}";

        for (int round = 1; round <= 2; round++) // 1 = fresh install, 2 = reinstall over it
        {
            var p = Process.Start(new ProcessStartInfo(setupExe, $"/sandbox \"{sandbox}\"") { UseShellExecute = false })!;
            try
            {
                var win = Wait(() => { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(p.MainWindowHandle) : null; }, 20);
                Check(win != null, $"round {round}: setup window opens");
                if (win == null) return 1;
                Thread.Sleep(1500);
                string expectButton = round == 1 ? "Install" : "Reinstall";
                var install = Wait(() => Button(win, expectButton), 5);
                Check(install != null, $"round {round}: '{expectButton}' button shown");
                Shot(win, Path.Combine(shots, $"setup-{round}-welcome.png"));
                if (install == null) return 1;
                ((InvokePattern)install.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                // The finish page (and its button) only exists once setup is done; Retry appears if it failed.
                var finish = Wait(() => ById(win, "FinishButton"), 120);
                Thread.Sleep(800);
                Shot(win, Path.Combine(shots, $"setup-{round}-done.png"));
                Check(finish?.Current.Name == "Finish" && ById(win, "RetryButton") == null, $"round {round}: install completes");
                if (finish != null) ((InvokePattern)finish.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                p.WaitForExit(5000);
            }
            finally { try { if (!p.HasExited) p.Kill(); } catch { } }
        }

        // Round 3: the way the auto-updater runs setup: silent, waiting for the open DiskImager to close, then reopening it.
        {
            using var stillOpen = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 4")
                { UseShellExecute = false, CreateNoWindow = true })!;
            var sw = Stopwatch.StartNew();
            using var silent = Process.Start(new ProcessStartInfo(setupExe, $"/silent /sandbox \"{sandbox}\" /waitpid {stillOpen.Id} /relaunch")
                { UseShellExecute = false })!;
            bool done = silent.WaitForExit(120000);
            bool waited = stillOpen.HasExited;
            Check(done && silent.ExitCode == 0, $"update mode: silent install finished (exit code {(done ? silent.ExitCode : -1)}, {sw.Elapsed.TotalSeconds:0.0} s)");
            Check(waited, "update mode: setup waited for the open DiskImager to close before installing");
            Check(File.Exists(Path.Combine(sandbox, "Program Files", "DiskImager", "relaunched.txt")), "update mode: DiskImager is reopened afterwards");
            string setupLog = Path.Combine(sandbox, "DiskImagerSetup.log");
            string logText = File.Exists(setupLog) ? File.ReadAllText(setupLog) : "";
            Check(logText.Contains("Waiting for DiskImager") && logText.Contains("Installed."), "update mode: setup log records the wait and the install");
        }

        string exe = Path.Combine(sandbox, "Program Files", "DiskImager", "DiskImager.exe");
        Check(File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).FileVersion?.StartsWith(version) == true, $"DiskImager.exe installed (version {version})");
        Check(File.Exists(Path.Combine(sandbox, "Start Menu", "DiskImager.lnk")), "Start menu shortcut created");
        Check(File.Exists(Path.Combine(sandbox, "Desktop", "DiskImager.lnk")), "desktop shortcut created");
        using (var k = Registry.CurrentUser.OpenSubKey(TestKey))
        {
            Check(k?.GetValue("DisplayName") as string == "DiskImager" && k.GetValue("Publisher") as string == "TechSource Digital"
                  && k.GetValue("DisplayVersion") as string == version, $"Installed-apps entry: name, publisher, version {version}");
            Check((k?.GetValue("UninstallString") as string)?.Contains("--uninstall") == true, "Installed-apps entry has an uninstall command");
        }
        Check(!Directory.EnumerateFiles(Path.Combine(sandbox, "Program Files", "DiskImager"), "*.new").Any(), "no leftover temporary files");

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\TechSource Digital\DiskImagerSetupTest", false);
        Console.WriteLine(failures == 0 ? "\nSETUP TESTS PASSED" : $"\n{failures} SETUP TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static AutomationElement? Button(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name)));

    static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    static T? Wait<T>(Func<T?> f, int seconds) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do { try { var r = f(); if (r != null) return r; } catch { } Thread.Sleep(300); } while (DateTime.UtcNow < until);
        return null;
    }

    static void Shot(AutomationElement w, string file)
    {
        try
        {
            var r = w.Current.BoundingRectangle;
            using var bmp = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }
}
