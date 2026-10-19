namespace DiskImager.Core;

/// <summary>
/// Finds scheduled backups that didn't start on time (PC off, task deleted/disabled, program missing)
/// and emails about them. Runs from its own scheduled task at startup, on wake from sleep and daily:
/// DiskImager.exe --check-missed
/// </summary>
public static class MissedCheck
{
    /// <summary>A run counts as on time if it started within this long after its scheduled time.</summary>
    public static readonly TimeSpan OnTimeWindow = TimeSpan.FromMinutes(20);

    public sealed record Miss(BackupJob Job, DateTime ScheduledLocal, int Count);

    /// <summary>Works out which jobs have an unreported missed run. Pure logic, so it can be tested.</summary>
    public static List<Miss> Find(IEnumerable<BackupJob> jobs, DateTime nowLocal)
    {
        var misses = new List<Miss>();
        var upTo = nowLocal - OnTimeWindow; // give a run that's due right now time to start
        foreach (var job in jobs)
        {
            if (job.ScheduleSinceUtc == null || job.RegisteredTasks.Count == 0) continue;
            var since = job.ScheduleSinceUtc.Value.ToLocalTime();
            var attempts = job.RecentAttemptsUtc.Select(a => a.ToLocalTime()).ToList();
            var reported = job.MissedNotifiedForUtc?.ToLocalTime() ?? DateTime.MinValue;

            bool OnTime(DateTime due) => attempts.Any(a => a >= due.AddMinutes(-2) && a <= due + OnTimeWindow);

            var due = job.Full.Occurrences(since, upTo).Concat(job.Incremental.Occurrences(since, upTo))
                .Distinct().OrderBy(d => d).ToList();
            // Only runs after the last one that happened on time matter; older gaps are history.
            var lastOnTime = due.Where(OnTime).DefaultIfEmpty(DateTime.MinValue).Max();
            var inARow = due.Where(d => d > lastOnTime && !OnTime(d)).ToList();
            var unreported = inARow.Where(d => d > reported).ToList();
            if (unreported.Count == 0) continue;

            misses.Add(new Miss(job, unreported[^1], inARow.Count));
        }
        return misses;
    }

    public static int Run(Logger log)
    {
        try
        {
            var jobs = JobStore.Load();

            // Jobs saved before this feature existed start tracking from now (no false alarms).
            foreach (var j in jobs.Where(j => j.ScheduleSinceUtc == null && j.RegisteredTasks.Count > 0))
                JobStore.Update(j.Id, x => x.ScheduleSinceUtc = DateTime.UtcNow);

            foreach (var miss in Find(JobStore.Load(), DateTime.Now))
            {
                log.Warn($"Job '{miss.Job.Name}' missed its scheduled run at {miss.ScheduledLocal:yyyy-MM-dd HH:mm}" +
                         (miss.Count > 1 ? $" ({miss.Count} runs in a row)." : "."));
                Notifier.JobMissed(miss.Job, miss.ScheduledLocal, miss.Count, log);
                JobStore.Update(miss.Job.Id, j =>
                {
                    j.MissedNotifiedForUtc = miss.ScheduledLocal.ToUniversalTime();
                    if (j.RecentAttemptsUtc.Count == 0 || j.RecentAttemptsUtc.Max() < j.MissedNotifiedForUtc)
                        j.LastResult = $"MISSED – was due {miss.ScheduledLocal:g}";
                });
            }
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Missed-backup check failed: " + ex.Message);
            return 1;
        }
    }
}
