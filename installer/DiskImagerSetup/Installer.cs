using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace DiskImagerSetup
{
    /// <summary>Where things go. The test sandbox points everything at a temp folder and HKCU.</summary>
    public sealed class InstallTarget
    {
        public string InstallDir;
        public string StartMenuDir;
        public string DesktopDir;
        public RegistryKey RegistryRoot;
        public string UninstallKeyPath;
        public string DataFolder;
        public bool RunPostInstall = true;
        /// <summary>Optional labels for this PC, from /client and /location.</summary>
        public string Client, Location;

        public string ExePath => Path.Combine(InstallDir, "DiskImager.exe");

        public static InstallTarget Real() => new InstallTarget
        {
            InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DiskImager"),
            StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            RegistryRoot = Registry.LocalMachine,
            UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DiskImager",
            DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskImager"),
        };

        public static InstallTarget Sandbox(string root) => new InstallTarget
        {
            InstallDir = Path.Combine(root, "Program Files", "DiskImager"),
            StartMenuDir = Path.Combine(root, "Start Menu"),
            DesktopDir = Path.Combine(root, "Desktop"),
            RegistryRoot = Registry.CurrentUser,
            UninstallKeyPath = @"Software\TechSource Digital\DiskImagerSetupTest\Uninstall\DiskImager",
            DataFolder = Path.Combine(root, "ProgramData", "DiskImager"),
            RunPostInstall = false,
        };
    }

    /// <summary>A problem the user can fix and retry (DiskImager open, backup running…).</summary>
    public sealed class RetryableException : Exception
    {
        public RetryableException(string message) : base(message) { }
    }

    public static class Installer
    {
        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

        public static string InstalledVersion(InstallTarget t)
        {
            using (var k = t.RegistryRoot.OpenSubKey(t.UninstallKeyPath))
                return k?.GetValue("DisplayVersion") as string;
        }

        public static void Install(InstallTarget t, bool desktopShortcut, bool startMenuShortcut, Action<string, double> progress)
        {
            progress("Checking for a running DiskImager...", 0.05);
            CloseRunning(t);

            progress("Copying DiskImager...", 0.15);
            Directory.CreateDirectory(t.InstallDir);
            string tmp = t.ExePath + ".new";
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe"))
            {
                if (payload == null) throw new InvalidOperationException("This setup file is damaged (the program inside it is missing). Download it again.");
                using (var f = File.Create(tmp))
                {
                    var buf = new byte[1 << 20];
                    long total = payload.Length, done = 0;
                    int n;
                    while ((n = payload.Read(buf, 0, buf.Length)) > 0)
                    {
                        f.Write(buf, 0, n);
                        done += n;
                        progress("Copying DiskImager...", 0.15 + 0.6 * done / Math.Max(1, total));
                    }
                }
            }
            ReplaceFile(tmp, t.ExePath);

            progress("Creating shortcuts...", 0.8);
            string startMenuLnk = Path.Combine(t.StartMenuDir, "DiskImager.lnk");
            string desktopLnk = Path.Combine(t.DesktopDir, "DiskImager.lnk");
            if (startMenuShortcut) CreateShortcut(startMenuLnk, t.ExePath);
            else TryDelete(startMenuLnk);
            if (desktopShortcut) CreateShortcut(desktopLnk, t.ExePath);
            else TryDelete(desktopLnk);

            progress("Adding DiskImager to Installed apps...", 0.88);
            using (var k = t.RegistryRoot.CreateSubKey(t.UninstallKeyPath))
            {
                k.SetValue("DisplayName", "DiskImager");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "TechSource Digital");
                k.SetValue("DisplayIcon", t.ExePath + ",0");
                k.SetValue("InstallLocation", t.InstallDir);
                k.SetValue("UninstallString", $"\"{t.ExePath}\" --uninstall");
                k.SetValue("URLInfoAbout", "https://e-techsource.com");
                k.SetValue("HelpLink", "https://e-techsource.com");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(t.ExePath).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            if (t.RunPostInstall)
            {
                // Existing jobs get their scheduled tasks (and the helper tasks) re-created for this version.
                if (t.Client != null || t.Location != null)
                {
                    progress("Labelling this PC...", 0.92);
                    string args = "--configure" + (t.Client != null ? $" --client \"{t.Client.Replace("\"", "")}\"" : "") +
                                  (t.Location != null ? $" --location \"{t.Location.Replace("\"", "")}\"" : "");
                    RunAndWait(t.ExePath, args, TimeSpan.FromMinutes(1));
                }
                progress("Updating scheduled backups...", 0.94);
                RunAndWait(t.ExePath, "--reschedule", TimeSpan.FromMinutes(2));
            }
            progress("Done", 1);
        }

        /// <summary>Tray helpers close themselves; an open DiskImager window or a running backup must finish first.</summary>
        private static void CloseRunning(InstallTarget t)
        {
            if (BackupRunning(t))
                throw new RetryableException("A DiskImager backup is running or about to start. Wait for it to finish, then click Retry.");

            try
            {
                string requests = Path.Combine(t.DataFolder, "Requests");
                if (Directory.Exists(requests))
                    File.WriteAllText(Path.Combine(requests, "agents-exit.flag"), DateTime.UtcNow.ToString("O"));
            }
            catch { }

            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until)
            {
                var open = Running(t).Where(p => p.MainWindowHandle != IntPtr.Zero).ToList();
                var helpers = Running(t).Where(p => p.MainWindowHandle == IntPtr.Zero).ToList();
                if (open.Count > 0)
                    throw new RetryableException("DiskImager is open. Close it, then click Retry.");
                if (helpers.Count == 0) return;
                Thread.Sleep(500);
            }
            // Helpers that didn't exit (e.g. an older version without the exit signal): stop them.
            foreach (var p in Running(t))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        private static Process[] Running(InstallTarget t) =>
            Process.GetProcessesByName("DiskImager").Where(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, t.ExePath, StringComparison.OrdinalIgnoreCase); }
                catch { return true; } // can't tell (another user's process): treat as ours to be safe
            }).ToArray();

        private static bool BackupRunning(InstallTarget t)
        {
            try
            {
                return Directory.EnumerateFiles(t.DataFolder, "status-*.json").Any(f =>
                {
                    string text = File.ReadAllText(f);
                    return !text.Contains("\"Phase\":\"Finished\"") && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(3);
                });
            }
            catch { return false; }
        }

        private static void ReplaceFile(string source, string dest)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(source, dest);
                    return;
                }
                catch (Exception) when (attempt < 10)
                {
                    Thread.Sleep(500);
                }
                catch (Exception)
                {
                    TryDelete(source);
                    throw new RetryableException("DiskImager's program file is in use (a backup may be running). Wait a moment, then click Retry.");
                }
            }
        }

        private static void CreateShortcut(string path, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = "DiskImager – disk imaging, cloning and backup. Powered by TechSource Digital.";
            lnk.Save();
        }

        private static void RunAndWait(string exe, string args, TimeSpan timeout)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }))
                    p?.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
