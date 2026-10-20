using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using DiskImager.Core;

namespace DiskImager;

public partial class App : Application
{
    private Mutex? _agentMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Rescue USB (Windows PE): the restore menu.
        if (e.Args.Contains("--rescue"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Rescue.Run());
            return;
        }

        // Scheduled runs (as SYSTEM): no window, just do the job and exit.
        if (e.Args.Contains("--run-job"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = JobRunner.RunScheduled(e.Args);
            Shutdown(code);
            return;
        }
        if (e.Args.Contains("--check-missed"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(MissedCheck.Run(new Logger()));
            return;
        }
        if (e.Args.Contains("--health-check"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(HealthMonitor.Run(new Logger()));
            return;
        }
        if (e.Args.Contains("--weekly-report"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var log = new Logger();
            // Runs at the due time, at startup and hourly: quiet unless it sends (or can't).
            try { WeeklyReport.SendScheduled(log); Shutdown(0); }
            catch (Exception ex) { log.Error("Weekly report: " + ex.Message); Shutdown(1); }
            return;
        }

        // Tray helper: runs as the signed-in user (no admin needed), one per session.
        if (e.Args.Contains("--agent"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool first;
            try { _agentMutex = new Mutex(true, @"Local\DiskImager.TrayAgent", out first); }
            catch (UnauthorizedAccessException) { first = false; }
            if (!first)
            {
                Shutdown(0);
                return;
            }
            DispatcherUnhandledException += (_, ex) => ex.Handled = true; // never bother the user from the tray
            new TrayAgent().Start();
            return;
        }

        // Only one DiskImager window at a time (checked before the admin prompt so a second click doesn't ask for it).
        bool opensWindow = !e.Args.Any(a => a is "--cli" or "--configure" or "--reschedule" or "--uninstall");
        if (opensWindow && MainWindowRunning())
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AlreadyRunning();
            Shutdown(0);
            return;
        }

        // The main window needs admin rights (raw disk access, snapshots, Task Scheduler).
        // The exe itself doesn't demand them, so the tray helper above can run as a normal user.
        if (!IsElevated() && !e.Args.Contains("--no-elevate"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", e.Args.Select(a => $"\"{a}\"")),
                });
            }
            catch (Win32Exception)
            {
                AppMessage.Show("DiskImager needs administrator permission to read and write disks. Please open it again and choose Yes.",
                    "DiskImager", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown(0);
            return;
        }

        // Command line for scripts and admins (see Core\Cli.cs).
        if (e.Args.Contains("--cli"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Cli.Run(e.Args));
            return;
        }

        // Set by setup: DiskImagerSetup.exe /silent /client "ACME Pte Ltd" /location "Front desk"
        if (e.Args.Contains("--configure"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string? Arg(string name) { int i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
            var s = AppSettings.Load();
            if (Arg("--client") is string client) s.Report.ClientName = client.Trim();
            if (Arg("--location") is string location) s.Report.Location = location.Trim();
            s.Save();
            new Logger().Info($"Setup: client \"{s.Report.ClientName}\", location \"{s.Report.Location}\".");
            Shutdown(0);
            return;
        }

        // Run by setup after installing or upgrading.
        if (e.Args.Contains("--reschedule"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Uninstaller.Reschedule(new Logger());
            Shutdown(0);
            return;
        }

        // Run from Settings → Apps → Uninstall.
        if (e.Args.Contains("--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunUninstall();
            Shutdown(0);
            return;
        }

        // Claim the one-window lock. A window that's just closing (e.g. restarting after an update) gets a moment to finish.
        if (!ClaimMainWindow())
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AlreadyRunning();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            AppMessage.Show(ex.Exception.Message, "DiskImager", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static void RunUninstall()
    {
        const string title = "Uninstall DiskImager";
        if (Uninstaller.BackupInProgress())
        {
            AppMessage.Show("A scheduled backup is running or about to start. Wait for it to finish, then uninstall again.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AppMessage.Show("Remove DiskImager from this computer?\n\nScheduled backups will stop. Your backup images are not deleted.",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool removeSettings = AppMessage.Show(
            "Also delete DiskImager's settings, backup jobs and logs?\n\nChoose No to keep them, for example if you'll reinstall later.",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            Uninstaller.Uninstall(removeSettings, new Logger());
            AppMessage.Show("DiskImager has been removed.", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppMessage.Show("DiskImager couldn't be fully removed: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Global: also covers another user signed in to the same PC (e.g. a server with several remote sessions),
    // since two windows working on the same disks at once is never safe.
    private const string MainWindowMutex = @"Global\DiskImager.MainWindow";
    private Mutex? _mainMutex;

    private static bool MainWindowRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MainWindowMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // exists, owned by an elevated window
    }

    private bool ClaimMainWindow()
    {
        try
        {
            _mainMutex = new Mutex(false, MainWindowMutex);
            try { return _mainMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { return true; } // the previous window crashed; the lock is ours now
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Tells the user DiskImager is already open and offers to bring that window to the front if it's in this session.</summary>
    private static void AlreadyRunning()
    {
        IntPtr existing = IntPtr.Zero;
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!))
                // The main window only: tray helpers in this session may have small windows of their own.
                .Where(p => p.Id != Environment.ProcessId && p.SessionId == session && p.MainWindowHandle != IntPtr.Zero
                            && p.MainWindowTitle.StartsWith("DiskImager – Powered", StringComparison.Ordinal))
                .Select(p => p.MainWindowHandle).FirstOrDefault();
        }
        catch { }
        var dlg = new AlreadyRunningWindow(canSwitch: existing != IntPtr.Zero);
        dlg.ShowDialog();
        if (dlg.SwitchChosen && existing != IntPtr.Zero)
        {
            if (IsIconic(existing)) ShowWindow(existing, 9); // SW_RESTORE
            SetForegroundWindow(existing);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
