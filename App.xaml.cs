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
                MessageBox.Show("DiskImager needs administrator permission to read and write disks. Please open it again and choose Yes.",
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

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "DiskImager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show("A scheduled backup is running or about to start. Wait for it to finish, then uninstall again.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show("Remove DiskImager from this computer?\n\nScheduled backups will stop. Your backup images are not deleted.",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool removeSettings = MessageBox.Show(
            "Also delete DiskImager's settings, backup jobs and logs?\n\nChoose No to keep them, for example if you'll reinstall later.",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            Uninstaller.Uninstall(removeSettings, new Logger());
            MessageBox.Show("DiskImager has been removed.", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("DiskImager couldn't be fully removed: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
