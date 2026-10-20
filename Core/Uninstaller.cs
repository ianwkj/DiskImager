using System.Diagnostics;
using Microsoft.Win32;

namespace DiskImager.Core;

/// <summary>
/// Install-related actions run by the app itself:
///  DiskImager.exe --reschedule  (run by setup) re-registers every job's scheduled tasks and the helper tasks;
///  DiskImager.exe --uninstall   (from Settings → Apps) removes tasks, shortcuts, the Apps entry and program files.
/// Backup images are never touched.
/// </summary>
public static class Uninstaller
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DiskImager";

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DiskImager.lnk");

    public static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "DiskImager.lnk");

    /// <summary>Re-creates every job's Windows tasks (after an install or upgrade) and the helper tasks.</summary>
    public static void Reschedule(Logger log)
    {
        var jobs = JobStore.Load();
        foreach (var job in jobs)
        {
            try { TaskSchedulerService.Register(job, log); }
            catch (Exception ex) { log.Warn($"Couldn't re-schedule '{job.Name}': {ex.Message}"); }
        }
        JobStore.Save(jobs);
        try { TaskSchedulerService.SyncMissedCheck(log); }
        catch (Exception ex) { log.Warn("Couldn't set up the helper tasks: " + ex.Message); }
        try { TaskSchedulerService.SyncReportTask(log); }
        catch (Exception ex) { log.Warn("Couldn't set up the weekly report task: " + ex.Message); }
        try { TaskSchedulerService.SyncHealthTask(log); }
        catch (Exception ex) { log.Warn("Couldn't set up the disk health check: " + ex.Message); }
        log.Info($"Setup: {jobs.Count} job(s) re-scheduled.");
    }

    /// <summary>True while a scheduled backup is running or about to start (uninstalling now would break it).</summary>
    public static bool BackupInProgress() => BackgroundStatus.ReadAll().Any();

    public static void Uninstall(bool removeSettings, Logger log)
    {
        // 1. Scheduled tasks for every job, plus the missed-backup check and tray helper.
        var jobs = JobStore.Load();
        foreach (var job in jobs) TaskSchedulerService.Unregister(job);
        foreach (var j in jobs) j.RegisteredTasks.Clear();
        if (!removeSettings) JobStore.Save(jobs);
        TaskSchedulerService.RemoveHelperTasks();

        // 2. Tray helpers in every session close themselves.
        AgentChannel.AskAgentsToExit();

        // 3. Shortcuts and the Settings → Apps entry.
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        // 4. Settings, jobs and logs (only if asked). Backup images live elsewhere and are never deleted.
        if (removeSettings)
            try { Directory.Delete(JobStore.DataFolder, recursive: true); } catch { }
        else
            log.Info("DiskImager was uninstalled; settings, jobs and logs were kept.");

        // 5. Program files: this exe is running, so remove the folder a few seconds after it exits.
        string dir = AppContext.BaseDirectory.TrimEnd('\\');
        if (string.Equals(dir, TaskSchedulerService.InstallFolder, StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 6 > nul & rmdir /s /q \"{dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
    }
}
