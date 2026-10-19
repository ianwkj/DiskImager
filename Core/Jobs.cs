using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskImager.Core;

public enum ScheduleKind { None, Daily, Weekly, Monthly }

// Sleep and Hibernate were removed as choices in 1.2.1. They stay here so older jobs.json files still load;
// JobStore.Load turns them into None.
public enum PostAction { None, Shutdown, Restart, Sleep, Hibernate }

public sealed class Schedule
{
    public ScheduleKind Kind { get; set; }
    public string Time { get; set; } = "22:00";
    public List<DayOfWeek> Days { get; set; } = new();
    public int DayOfMonth { get; set; } = 1;

    public bool OccursOn(DateTime date) => Kind switch
    {
        ScheduleKind.Daily => true,
        ScheduleKind.Weekly => Days.Contains(date.DayOfWeek),
        ScheduleKind.Monthly => date.Day == DayOfMonth,
        _ => false,
    };

    /// <summary>Scheduled run times in (after, upTo], local time, oldest first (looks back at most 62 days).</summary>
    public IEnumerable<DateTime> Occurrences(DateTime after, DateTime upTo)
    {
        if (Kind == ScheduleKind.None || !TimeSpan.TryParse(Time, out var t)) yield break;
        var start = after.Date > upTo.Date.AddDays(-62) ? after.Date : upTo.Date.AddDays(-62);
        for (var d = start; d <= upTo.Date; d = d.AddDays(1))
        {
            var at = d + t;
            if (at > after && at <= upTo && OccursOn(d)) yield return at;
        }
    }

    public string Describe() => Kind switch
    {
        ScheduleKind.Daily => $"Daily at {Time}",
        ScheduleKind.Weekly => $"Weekly on {string.Join(", ", Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]))} at {Time}",
        ScheduleKind.Monthly => $"Monthly on day {DayOfMonth} at {Time}",
        _ => "Not scheduled",
    };
}

public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My backup";
    public string SourceDiskId { get; set; } = "";
    public string SourceDiskDescription { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? NetUser { get; set; }
    public string? NetPasswordProtected { get; set; }
    public CompressionChoice Compression { get; set; } = CompressionChoice.Fast;
    public Schedule Full { get; set; } = new() { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Sunday } };
    public Schedule Incremental { get; set; } = new() { Kind = ScheduleKind.Daily };
    public int KeepFullSets { get; set; } = 2;
    public int MaxIncrementals { get; set; } = 6;
    public PostAction After { get; set; } = PostAction.None;
    public NotifyWhen Notify { get; set; } = NotifyWhen.OnFailure;
    /// <summary>Re-read and check each backup straight after it's made.</summary>
    public bool VerifyAfter { get; set; }
    /// <summary>Folders left out of the image, e.g. C:\Users\*\Downloads (restored as empty folders).</summary>
    public List<string> ExcludeFolders { get; set; } = new();
    public bool WakeToRun { get; set; } = true;
    public List<string> RegisteredTasks { get; set; } = new();
    public DateTime? LastRunUtc { get; set; }
    public string? LastResult { get; set; }
    public DateTime? LastSuccessUtc { get; set; }

    // Missed-run tracking.
    /// <summary>Runs scheduled before this time (job created / schedule last saved) are never reported as missed.</summary>
    public DateTime? ScheduleSinceUtc { get; set; }
    /// <summary>Start times of the most recent runs (scheduled or manual), newest last.</summary>
    public List<DateTime> RecentAttemptsUtc { get; set; } = new();
    /// <summary>The latest scheduled time already reported as missed, so each miss is emailed once.</summary>
    public DateTime? MissedNotifiedForUtc { get; set; }

    public BackupJob Clone() => JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(this, JobStore.Json), JobStore.Json)!;
}

/// <summary>Jobs live in %ProgramData%\DiskImager\jobs.json so scheduled runs (as SYSTEM) see the same list.</summary>
public static class JobStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DiskImager.NetCredential.v1");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskImager");

    private static string JobsFile => System.IO.Path.Combine(DataFolder, "jobs.json");

    public static List<BackupJob> Load()
    {
        try
        {
            return File.Exists(JobsFile) ? Parse(File.ReadAllText(JobsFile)) : new();
        }
        catch
        {
            return new();
        }
    }

    public static List<BackupJob> Parse(string json)
    {
        var jobs = JsonSerializer.Deserialize<List<BackupJob>>(json, Json) ?? new();
        foreach (var j in jobs)
            if (j.After is PostAction.Sleep or PostAction.Hibernate) j.After = PostAction.None;
        return jobs;
    }

    public static void Save(List<BackupJob> jobs)
    {
        Directory.CreateDirectory(DataFolder);
        string tmp = JobsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(jobs, Json));
        File.Move(tmp, JobsFile, overwrite: true);
    }

    public static void Update(Guid id, Action<BackupJob> change)
    {
        var jobs = Load();
        var job = jobs.FirstOrDefault(j => j.Id == id);
        if (job == null) return;
        change(job);
        Save(jobs);
    }

    // Machine-scope DPAPI: the scheduled task runs as SYSTEM and must be able to decrypt.
    public static string? Protect(string? password) =>
        string.IsNullOrEmpty(password) ? null :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));

    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.LocalMachine));
        }
        catch
        {
            return null;
        }
    }
}
