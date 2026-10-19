using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;

namespace DiskImagerSetup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // /sandbox <folder>: installs into a test folder (HKCU, no admin) — used by the automated tests.
            int s = Array.IndexOf(e.Args, "/sandbox");

            // /silent: install with the defaults and no window (for scripted installs, e.g. on servers).
            // Exit code 0 = installed, 1 = failed, 2 = not run as administrator. Log: %TEMP%\DiskImagerSetup.log
            // Used by DiskImager's updater with /waitpid <id> (wait for that DiskImager to close first)
            // and /relaunch (open DiskImager again afterwards).
            if (e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var silentTarget = s >= 0 && s + 1 < e.Args.Length ? InstallTarget.Sandbox(e.Args[s + 1]) : null;
                int wp = Array.FindIndex(e.Args, a => a.Equals("/waitpid", StringComparison.OrdinalIgnoreCase));
                int waitPid = wp >= 0 && wp + 1 < e.Args.Length && int.TryParse(e.Args[wp + 1], out int pid) ? pid : 0;
                bool relaunch = e.Args.Any(a => a.Equals("/relaunch", StringComparison.OrdinalIgnoreCase));
                string Arg(string name)
                {
                    int i = Array.FindIndex(e.Args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
                }
                Shutdown(RunSilent(silentTarget, waitPid, relaunch, Arg("/client"), Arg("/location")));
                return;
            }
            InstallTarget target;
            if (s >= 0 && s + 1 < e.Args.Length) target = InstallTarget.Sandbox(e.Args[s + 1]);
            else
            {
                if (!IsElevated())
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                            Arguments = string.Join(" ", e.Args.Select(a => $"\"{a}\"")),
                        });
                    }
                    catch (Win32Exception)
                    {
                        MessageBox.Show("Installing DiskImager needs administrator permission. Run setup again and choose Yes.",
                            "DiskImager Setup", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    Shutdown(0);
                    return;
                }
                target = InstallTarget.Real();
            }

            var w = new SetupWindow(target);
            MainWindow = w;
            w.Show();
        }

        /// <param name="sandbox">Test target, or null for the real install (needs admin).</param>
        private static int RunSilent(InstallTarget sandbox, int waitPid, bool relaunch, string client, string location)
        {
            var target = sandbox ?? InstallTarget.Real();
            target.Client = client;
            target.Location = location;
            string log = sandbox != null
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(target.DataFolder, "..", "..", "DiskImagerSetup.log")) // sandbox root
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskImagerSetup.log");
            void Log(string m)
            {
                try { System.IO.File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}{Environment.NewLine}"); } catch { }
            }
            if (sandbox == null && !IsElevated())
            {
                Log("Silent install needs to be run as administrator.");
                return 2;
            }

            if (waitPid > 0)
            {
                Log($"Waiting for DiskImager (process {waitPid}) to close.");
                try
                {
                    using (var p = Process.GetProcessById(waitPid))
                        if (!p.WaitForExit(60000)) Log("DiskImager is still open after 60 seconds; trying anyway.");
                }
                catch (ArgumentException) { } // already closed
            }

            int code;
            try
            {
                Log($"Installing DiskImager {Installer.Version} silently.");
                // DiskImager may still be releasing its files for a moment after closing: retry a few times.
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        Installer.Install(target, desktopShortcut: true, startMenuShortcut: true, (msg, _) => Log(msg));
                        break;
                    }
                    catch (RetryableException ex) when (attempt < 5)
                    {
                        Log($"{ex.Message} (attempt {attempt}; retrying in 5 seconds)");
                        System.Threading.Thread.Sleep(5000);
                    }
                }
                Log("Installed.");
                code = 0;
            }
            catch (Exception ex)
            {
                Log("Install failed: " + ex.Message);
                code = 1;
            }

            // Reopen DiskImager either way, so an update never leaves the user without the app.
            if (relaunch && File.Exists(target.ExePath))
            {
                if (sandbox != null)
                {
                    // Tests: record instead of starting the app (it would ask for administrator rights).
                    File.WriteAllText(System.IO.Path.Combine(target.InstallDir, "relaunched.txt"), target.ExePath);
                    Log("Reopening DiskImager (sandbox: recorded only).");
                }
                else
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(target.ExePath) { UseShellExecute = true, WorkingDirectory = target.InstallDir });
                        Log("Reopened DiskImager.");
                    }
                    catch (Exception ex) { Log("Couldn't reopen DiskImager: " + ex.Message); }
                }
            }
            return code;
        }

        private static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
