using System;
using System.ComponentModel;
using System.Diagnostics;
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

            // /silent: install with the defaults and no window (for scripted installs, e.g. on servers).
            // Exit code 0 = installed, 1 = failed, 2 = not run as administrator. Log: %TEMP%\DiskImagerSetup.log
            if (e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(RunSilent());
                return;
            }

            // /sandbox <folder>: installs into a test folder (HKCU, no admin) — used by the automated tests.
            int s = Array.IndexOf(e.Args, "/sandbox");
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

        private static int RunSilent()
        {
            string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskImagerSetup.log");
            void Log(string m)
            {
                try { System.IO.File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}{Environment.NewLine}"); } catch { }
            }
            if (!IsElevated())
            {
                Log("Silent install needs to be run as administrator.");
                return 2;
            }
            try
            {
                Log($"Installing DiskImager {Installer.Version} silently.");
                Installer.Install(InstallTarget.Real(), desktopShortcut: true, startMenuShortcut: true, (msg, _) => Log(msg));
                Log("Installed.");
                return 0;
            }
            catch (Exception ex)
            {
                Log("Install failed: " + ex.Message);
                return 1;
            }
        }

        private static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
