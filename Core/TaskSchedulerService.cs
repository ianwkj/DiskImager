using System.Diagnostics;
using System.Security;
using System.Text;

namespace DiskImager.Core;

/// <summary>Registers jobs with Windows Task Scheduler (running as SYSTEM) via schtasks.exe.</summary>
public static class TaskSchedulerService
{
    public static string InstallFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DiskImager");

    public static void Register(BackupJob job, Logger log)
    {
        Unregister(job);
        if (job.Full.Kind == ScheduleKind.None && job.Incremental.Kind == ScheduleKind.None) return;

        string exe = EnsureInstalled(log);
        if (job.Full.Kind != ScheduleKind.None) job.RegisteredTasks.Add(Create(job, exe, incremental: false));
        if (job.Incremental.Kind != ScheduleKind.None) job.RegisteredTasks.Add(Create(job, exe, incremental: true));
        log.Info($"Scheduled job '{job.Name}': full {job.Full.Describe()}; incremental {job.Incremental.Describe()}.");
    }

    public static void Unregister(BackupJob job)
    {
        foreach (var name in job.RegisteredTasks)
            RunSchtasks($"/Delete /TN \"{name}\" /F", throwOnError: false);
        job.RegisteredTasks.Clear();
    }

    /// <summary>Keeps the copy used by scheduled tasks up to date when a newer DiskImager is opened,
    /// and makes sure the helper tasks are registered and the tray helper is running.</summary>
    public static void RefreshInstalledCopy(Logger log)
    {
        if (!Environment.IsPrivilegedProcess) return; // can't write Program Files or Task Scheduler (UI tests run this way)
        bool jobs = JobStore.Load().Any(j => j.RegisteredTasks.Count > 0);
        if (!jobs && !AppSettings.Load().Health.Monitor) return;
        if (Directory.Exists(InstallFolder)) EnsureInstalled(log);
        SyncMissedCheck(log);
        try { SyncHealthTask(log); } catch (Exception ex) { log.Warn("Couldn't update the disk health task: " + ex.Message); }
        if (jobs)
            try { SyncReportTask(log); } catch (Exception ex) { log.Warn("Couldn't update the weekly report task: " + ex.Message); }
    }

    public const string MissedCheckTask = @"DiskImager\Missed backup check";
    public const string TrayAgentTask = @"DiskImager\Tray helper";
    public const string HealthTask = @"DiskImager\Disk health check";

    /// <summary>
    /// Registers (or removes) the helper tasks:
    ///  - the missed-backup check (3 min after startup, 3 min after wake, daily at 12:00), as SYSTEM, while there are scheduled jobs;
    ///  - the tray helper, started at every user's sign-in as that user (shows the countdown, progress and disk health
    ///    alerts), while there are scheduled jobs or disk health monitoring is on.
    /// Also starts the tray helper in this session if it isn't running.
    /// </summary>
    public static void SyncMissedCheck(Logger log)
    {
        bool jobs = JobStore.Load().Any(j => j.RegisteredTasks.Count > 0);
        bool health = AppSettings.Load().Health.Monitor;
        if (!jobs) RunSchtasks($"/Delete /TN \"{MissedCheckTask}\" /F", throwOnError: false);
        if (!jobs && !health)
        {
            RunSchtasks($"/Delete /TN \"{TrayAgentTask}\" /F", throwOnError: false);
            AgentChannel.AskAgentsToExit();
            return;
        }
        AgentChannel.EnsureFolder();
        string exe = EnsureInstalled(log);
        if (jobs) CreateFromXml(MissedCheckTask, BuildMissedCheckXml(exe));
        CreateFromXml(TrayAgentTask, BuildTrayAgentXml(exe));
        StartTrayAgent(exe);
    }

    /// <summary>Creates or removes the background disk health check to match the settings.</summary>
    public static void SyncHealthTask(Logger log)
    {
        var h = AppSettings.Load().Health;
        if (!h.Monitor)
        {
            RunSchtasks($"/Delete /TN \"{HealthTask}\" /F", throwOnError: false);
            return;
        }
        CreateFromXml(HealthTask, BuildHealthXml(EnsureInstalled(log), h));
        log.Info($"Disk health is checked {h.ScheduleText()}.");
    }

    /// <summary>Removes the missed-backup check, tray helper, report and disk health tasks (used by uninstall).</summary>
    public static void RemoveHelperTasks()
    {
        RunSchtasks($"/Delete /TN \"{MissedCheckTask}\" /F", throwOnError: false);
        RunSchtasks($"/Delete /TN \"{TrayAgentTask}\" /F", throwOnError: false);
        RunSchtasks($"/Delete /TN \"{ReportTask}\" /F", throwOnError: false);
        RunSchtasks($"/Delete /TN \"{HealthTask}\" /F", throwOnError: false);
    }

    /// <summary>
    /// The background disk health check, as SYSTEM. Hourly: every hour from 5 minutes after startup. Daily, weekly
    /// and monthly: at the chosen time (and day), run as soon as possible if the PC was off at that time.
    /// </summary>
    public static string BuildHealthXml(string exe, HealthSettings h)
    {
        if (!TimeSpan.TryParse(h.Time, out var t)) t = new TimeSpan(9, 0, 0);
        string today = $"{DateTime.Today:yyyy-MM-dd}T{t:hh\\:mm}:00";
        int dom = Math.Clamp(h.DayOfMonth, 1, 28);
        var every = h.Every;
        string triggers = every switch
        {
            HealthInterval.Day => $"""
                <CalendarTrigger>
                  <StartBoundary>{today}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
                """,
            HealthInterval.Week => $"""
                <CalendarTrigger>
                  <StartBoundary>{today}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek><DaysOfWeek><{h.Day} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>
                </CalendarTrigger>
                """,
            HealthInterval.Month => $"""
                <CalendarTrigger>
                  <StartBoundary>{today}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByMonth><DaysOfMonth><Day>{dom}</Day></DaysOfMonth><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonth>
                </CalendarTrigger>
                """,
            _ => $"""
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT5M</Delay>
                </BootTrigger>
                <TimeTrigger>
                  <StartBoundary>{DateTime.Now:yyyy-MM-dd}T{DateTime.Now:HH\:mm}:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <Repetition><Interval>PT1H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
                </TimeTrigger>
                """,
        };
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DiskImager: checks the health of this PC's drives {h.ScheduleText()} and emails you if it changes.</Description>
              </RegistrationInfo>
              <Triggers>
                {triggers}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--health-check</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void CreateFromXml(string name, string xml)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"diskimager-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            RunSchtasks($"/Create /TN \"{name}\" /XML \"{tmp}\" /F", throwOnError: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Starts the tray helper in the current session (it exits by itself if one is already running).</summary>
    public static void StartTrayAgent(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe, "--agent") { UseShellExecute = false, CreateNoWindow = true }); }
        catch { }
    }

    public static string BuildTrayAgentXml(string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>DiskImager tray helper: shows the countdown before scheduled backups and their progress.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <GroupId>S-1-5-32-545</GroupId>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>--agent</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public static string BuildMissedCheckXml(string exe)
    {
        const string wakeQuery =
            "&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"System\"&gt;&lt;Select Path=\"System\"&gt;" +
            "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]" +
            "&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DiskImager: emails you if a scheduled backup was missed (checked at startup, after sleep and daily).</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT3M</Delay>
                </BootTrigger>
                <EventTrigger>
                  <Enabled>true</Enabled>
                  <Subscription>{wakeQuery}</Subscription>
                  <Delay>PT3M</Delay>
                </EventTrigger>
                <CalendarTrigger>
                  <StartBoundary>{DateTime.Today:yyyy-MM-dd}T12:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--check-missed</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public const string ReportTask = @"DiskImager\Weekly report";

    /// <summary>Creates or removes the weekly report task to match this PC's settings.</summary>
    public static void SyncReportTask(Logger log)
    {
        var r = AppSettings.Load().Report;
        if (!r.SendWeekly)
        {
            RunSchtasks($"/Delete /TN \"{ReportTask}\" /F", throwOnError: false);
            return;
        }
        CreateFromXml(ReportTask, BuildReportXml(EnsureInstalled(log), r.Day, r.Time));
        log.Info($"Weekly report scheduled for {r.Day} at {r.Time}.");
    }

    public static string BuildReportXml(string exe, DayOfWeek day, string time)
    {
        if (!TimeSpan.TryParse(time, out var t)) t = new TimeSpan(8, 0, 0);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DiskImager: emails the weekly backup report for this office (the first PC that's on after the due time sends it).</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{DateTime.Today:yyyy-MM-dd}T{t:hh\:mm}:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek>
                    <DaysOfWeek><{day} /></DaysOfWeek>
                    <WeeksInterval>1</WeeksInterval>
                  </ScheduleByWeek>
                </CalendarTrigger>
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT5M</Delay>
                </BootTrigger>
                <TimeTrigger>
                  <StartBoundary>{DateTime.Today:yyyy-MM-dd}T00:17:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <Repetition><Interval>PT1H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--weekly-report</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string EnsureInstalled(Logger log)
    {
        string current = Environment.ProcessPath!;
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        string target = InstallFolder;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return current;
        string installedExe = System.IO.Path.Combine(target, System.IO.Path.GetFileName(current));

        var stale = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => (Src: f, Dest: System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, f))))
            .Where(x =>
            {
                var fi = new FileInfo(x.Dest);
                var si = new FileInfo(x.Src);
                return !fi.Exists || fi.Length != si.Length || fi.LastWriteTimeUtc != si.LastWriteTimeUtc;
            })
            .ToList();
        if (stale.Count == 0) return installedExe;

        // Tray helpers run from the installed copy; ask them to close so it can be replaced.
        AgentChannel.AskAgentsToExit();
        Exception? last = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                foreach (var (src, dest) in stale)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                }
                return installedExe;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(1000); // a helper is still closing, or a scheduled backup is using the file
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        if (File.Exists(installedExe))
        {
            log.Warn($"Could not update {target} ({last?.Message}); it's probably in use by a running backup. " +
                     "Open DiskImager again after the backup finishes to update it.");
            return installedExe;
        }
        log.Warn($"Could not copy DiskImager to {target} ({last?.Message}); the schedule will run it from {current}, so don't move that file.");
        return current;
    }

    private static string Create(BackupJob job, string exe, bool incremental)
    {
        string safe = new string(job.Name.Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray()).Trim();
        string name = $@"DiskImager\{safe} - {(incremental ? "Incremental" : "Full")}";
        string xml = BuildXml(job, exe, incremental);
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskimager-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            RunSchtasks($"/Create /TN \"{name}\" /XML \"{tmp}\" /F", throwOnError: true);
        }
        finally
        {
            File.Delete(tmp);
        }
        return name;
    }

    private static string BuildXml(BackupJob job, string exe, bool incremental)
    {
        var s = incremental ? job.Incremental : job.Full;
        string start = $"{DateTime.Today:yyyy-MM-dd}T{TimeSpan.Parse(s.Time):hh\\:mm}:00";
        string schedule = s.Kind switch
        {
            ScheduleKind.Daily => "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            ScheduleKind.Weekly => "<ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek>" +
                                   string.Concat(s.Days.Distinct().Select(d => $"<{d}/>")) + "</DaysOfWeek></ScheduleByWeek>",
            _ => $"<ScheduleByMonth><DaysOfMonth><Day>{s.DayOfMonth}</Day></DaysOfMonth><Months>" +
                 "<January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/>" +
                 "</Months></ScheduleByMonth>",
        };
        string args = $"--run-job {job.Id} --type {(incremental ? "incremental" : "full")}";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{SecurityElement.Escape($"DiskImager {(incremental ? "incremental" : "full")} backup: {job.Name}")}</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  {schedule}
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>{(job.WakeToRun ? "true" : "false")}</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{args}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void RunSchtasks(string args, bool throwOnError)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && throwOnError)
            throw new InvalidOperationException("Task Scheduler refused the job: " + err.Trim());
    }
}
