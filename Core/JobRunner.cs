namespace DiskImager.Core;

/// <summary>Only one disk operation at a time, across the UI and scheduled runs.</summary>
public sealed class OperationLock : IDisposable
{
    private readonly Mutex _mutex;

    private OperationLock(Mutex mutex) => _mutex = mutex;

    public static OperationLock? TryAcquire(TimeSpan wait)
    {
        Mutex m;
        try { m = new Mutex(false, @"Global\DiskImager.Operation"); }
        catch (UnauthorizedAccessException) { return null; }
        try
        {
            if (m.WaitOne(wait)) return new OperationLock(m);
        }
        catch (AbandonedMutexException)
        {
            return new OperationLock(m);
        }
        m.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

public static class JobRunner
{
    public static BackupResult RunJob(BackupJob job, bool incremental, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        NetworkShare.Connect(job.Destination, job.NetUser, JobStore.Unprotect(job.NetPasswordProtected), log);
        var disk = DiskEnumerator.FindDisk(job.SourceDiskId)
                   ?? throw new InvalidOperationException($"The source disk ({job.SourceDiskDescription}) is not connected.");
        string? password = JobStore.Unprotect(job.EncryptionPasswordProtected);
        if (job.EncryptionPasswordProtected != null && string.IsNullOrEmpty(password))
            throw new InvalidOperationException("The job's backup password couldn't be read on this PC. Edit the job and enter the password again.");
        var result = BackupEngine.Run(new BackupRequest
        {
            Disk = disk,
            DestinationFolder = job.Destination,
            JobName = job.Name,
            Incremental = incremental,
            Compression = job.Compression,
            MaxIncrementals = job.MaxIncrementals,
            KeepFullSets = job.KeepFullSets,
            DeferRetention = true,
            ExcludeFolders = job.ExcludeFolders,
            EncryptionPassword = password,
        }, progress, ct, log);
        // Verify first, then clean up: old backups are only deleted once the new one is known to be good.
        if (job.VerifyAfter) BackupEngine.VerifyNewImage(result, progress, ct, log, password);
        ImageCatalog.ApplyRetention(job.Destination, job.Name, job.KeepFullSets, result.ChainId, log);
        return result;
    }

    public static void RecordResult(Guid jobId, string result, bool success = false) =>
        JobStore.Update(jobId, j =>
        {
            j.LastRunUtc = DateTime.UtcNow;
            j.LastResult = result;
            if (success) j.LastSuccessUtc = j.LastRunUtc;
        });

    /// <summary>Notes that a run started (used to tell on-time runs from missed ones).</summary>
    public static void RecordAttempt(Guid jobId) =>
        JobStore.Update(jobId, j =>
        {
            j.RecentAttemptsUtc.Add(DateTime.UtcNow);
            if (j.RecentAttemptsUtc.Count > 30) j.RecentAttemptsUtc.RemoveRange(0, j.RecentAttemptsUtc.Count - 30);
        });

    /// <summary>Runs a saved job, records the outcome on the job and sends the email notification.
    /// Cancellation by the user is recorded but not emailed.</summary>
    public static BackupResult RunJobAndReport(BackupJob job, bool incremental, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        var started = DateTime.Now;
        RecordAttempt(job.Id);
        try
        {
            var r = RunJob(job, incremental, progress, ct, log);
            RecordResult(job.Id, $"OK – {(r.WasFull ? "Full" : "Incremental")}, {Util.FormatBytes(r.BytesWritten)}", success: true);
            Notifier.JobFinished(job, incremental, r, null, started, log);
            return r;
        }
        catch (OperationCanceledException)
        {
            RecordResult(job.Id, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            log.Error($"Job '{job.Name}' failed: {ex}");
            RecordResult(job.Id, "FAILED – " + ex.Message);
            Notifier.JobFinished(job, incremental, null, ex.Message, started, log);
            throw;
        }
    }

    /// <summary>Entry point for Task Scheduler: DiskImager.exe --run-job {id} --type full|incremental</summary>
    public static int RunScheduled(string[] args)
    {
        var log = new Logger();
        int i = Array.IndexOf(args, "--run-job");
        if (i < 0 || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var id))
        {
            log.Error("Scheduled run: missing or invalid job id.");
            return 2;
        }
        bool incremental = args.Any(a => a.Equals("incremental", StringComparison.OrdinalIgnoreCase));

        var job = JobStore.Load().FirstOrDefault(j => j.Id == id);
        if (job == null)
        {
            log.Error($"Scheduled run: job {id} no longer exists.");
            return 2;
        }

        // Record the start straight away (before any wait), so the missed-backup check sees it was on time.
        RecordAttempt(job.Id);

        if (incremental && job.Full.OccursOn(DateTime.Today) && job.Full.Time == job.Incremental.Time)
        {
            log.Info($"Job '{job.Name}': skipping incremental, the full backup is scheduled for the same time today.");
            return 0;
        }

        log.Info($"Scheduled {(incremental ? "incremental" : "full")} backup '{job.Name}' starting.");
        var triggeredUtc = DateTime.UtcNow;
        int code;
        AgentChannel.EnsureFolder();
        // Keep the PC awake for the whole run, including any wait for another operation to finish.
        using (new KeepAwake($"DiskImager: scheduled backup '{job.Name}' is running", log))
        using (var status = new BackgroundStatus.Publisher(job.Name, Describe(job)))
        {
            while (true)
            {
                var opLock = AcquireOrWait(job.Name, status, log);
                int postponeMinutes = 0;
                using (opLock)
                {
                    // Use the job as it is now, not as it was when this run was triggered (it may have been edited
                    // while we waited), and don't repeat a backup another run of this job just finished.
                    var current = opLock == null ? null : JobStore.Load().FirstOrDefault(j => j.Id == id);
                    if (opLock == null)
                    {
                        const string msg = "Skipped: another disk operation kept running for 12 hours.";
                        log.Error($"Job '{job.Name}': {msg}");
                        RecordResult(job.Id, msg);
                        Notifier.JobFinished(job, incremental, null, msg, DateTime.Now, log);
                        status.Finish(msg, false);
                        code = 3;
                        break;
                    }
                    if (current == null)
                    {
                        log.Info($"Job '{job.Name}' was deleted while this run was waiting; nothing to do.");
                        status.Finish("The job was deleted.", false);
                        return 0;
                    }
                    if (current.LastSuccessUtc > triggeredUtc)
                    {
                        log.Info($"Job '{current.Name}': skipped, it already completed a backup while this run was waiting.");
                        status.Finish("Already backed up a moment ago.", true);
                        return 0;
                    }
                    job = current;

                    // Give the signed-in user 10 seconds to start now, postpone or cancel.
                    var answer = AskUser(status, log);
                    if (answer?.Action == UserAction.Cancel)
                    {
                        log.Info($"Job '{job.Name}': cancelled by {answer.User} before it started.");
                        RecordResult(job.Id, $"Cancelled by {answer.User}");
                        status.Finish("Backup cancelled.", false);
                        return 0; // the user is at the PC: no shutdown/sleep afterwards
                    }
                    if (answer?.Action == UserAction.Postpone)
                    {
                        postponeMinutes = Math.Clamp(answer.Minutes, 1, 24 * 60);
                        log.Info($"Job '{job.Name}': postponed {postponeMinutes} minutes by {answer.User}.");
                    }
                    else
                    {
                        status.SetRunning();
                        using var cts = new CancellationTokenSource();
                        string? cancelledBy = null;
                        using var watcher = new Timer(_ =>
                        {
                            var c = AgentChannel.CancelRequested(Environment.ProcessId);
                            if (c != null) { cancelledBy = c.User; cts.Cancel(); }
                        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                        try
                        {
                            var r = RunJobAndReport(job, incremental, status, cts.Token, log);
                            status.Finish($"Backup completed: {Util.FormatBytes(r.BytesWritten)} in {Util.FormatDuration(r.Duration)}.", true);
                            code = 0;
                        }
                        catch (OperationCanceledException)
                        {
                            log.Info($"Job '{job.Name}': stopped by {cancelledBy ?? "the user"}.");
                            status.Finish("Backup stopped. Nothing was saved from this run.", false);
                            return 0;
                        }
                        catch (Exception ex)
                        {
                            status.Finish("Backup failed: " + ex.Message, false);
                            code = 1;
                        }
                        break;
                    }
                }

                // Postponed: the lock is released (other backups may run), then ask again later.
                var until = DateTime.UtcNow.AddMinutes(postponeMinutes);
                status.SetPostponed(until);
                while (DateTime.UtcNow < until) Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }
        PowerActions.Execute(job.After, log);
        return code;
    }

    public static readonly TimeSpan CountdownLength = TimeSpan.FromSeconds(10);

    /// <summary>Shows the countdown pop-up through the tray helper. Null = no answer (start), or nobody signed in.</summary>
    private static UserResponse? AskUser(BackgroundStatus.Publisher status, Logger log)
    {
        if (!AgentChannel.AnyAgentAlive()) return null;
        var promptId = Guid.NewGuid();
        status.SetCountdown(promptId, DateTime.UtcNow + CountdownLength);
        // A little extra time for the pop-up to appear and the answer to be written.
        var answer = AgentChannel.WaitForResponse(promptId, CountdownLength + TimeSpan.FromSeconds(3));
        if (answer?.Action == UserAction.Start) log.Info($"Started immediately by {answer.User}.");
        return answer;
    }

    private static OperationLock? AcquireOrWait(string jobName, BackgroundStatus.Publisher status, Logger log)
    {
        var opLock = OperationLock.TryAcquire(TimeSpan.Zero);
        if (opLock != null) return opLock;
        log.Info($"Job '{jobName}': another backup is running; this run will start when it finishes.");
        status.SetWaiting(true);
        var giveUp = DateTime.UtcNow.AddHours(12);
        while (opLock == null && DateTime.UtcNow < giveUp)
            opLock = OperationLock.TryAcquire(TimeSpan.FromSeconds(30));
        return opLock;
    }

    private static string Describe(BackupJob job) =>
        $"{job.SourceDiskDescription}  →  {job.Destination}";
}
