using System.Text.Json;

namespace DiskImager.Core;

public enum HealthInterval { Hour, Day, Week, Month }

/// <summary>Disk health monitoring and its email alerts (Settings → Disk health monitoring).</summary>
public sealed class HealthSettings
{
    /// <summary>Check every drive in the background (a SYSTEM scheduled task) and email on changes.</summary>
    public bool Monitor { get; set; } = true;
    public HealthInterval Every { get; set; } = HealthInterval.Hour;
    /// <summary>Time of day for daily, weekly and monthly checks (HH:mm).</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>Day of the week for weekly checks.</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    /// <summary>Day of the month for monthly checks (1–28, so every month has it).</summary>
    public int DayOfMonth { get; set; } = 1;

    public bool EmailOnStatusChange { get; set; } = true;
    public bool EmailOnLowLife { get; set; } = true;
    public int LowLifePercent { get; set; } = 20;
    public bool EmailOnHot { get; set; } = true;
    public int HotSsdC { get; set; } = 55;
    public int HotHddC { get; set; } = 50;
    public bool EmailOnBadSectors { get; set; } = true;
    public int BadSectorIncrease { get; set; } = 1;

    public static string Describe(HealthInterval i) => i switch
    {
        HealthInterval.Day => "1 day",
        HealthInterval.Week => "1 week",
        HealthInterval.Month => "1 month",
        _ => "1 hour",
    };

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>Exactly when the background check runs, in words.</summary>
    public string WhenText() => Every switch
    {
        HealthInterval.Day => $"Checks every day at {Time}, or soon after the PC is next turned on if it was off then.",
        HealthInterval.Week => $"Checks every {Day} at {Time}, or soon after the PC is next turned on if it was off then.",
        HealthInterval.Month => $"Checks on the {Ordinal(DayOfMonth)} of every month at {Time}, or soon after the PC is next turned on if it was off then.",
        _ => "Checks 5 minutes after the PC starts, then every hour while it's on.",
    };

    /// <summary>Short form for status lines: "every hour", "every Monday at 09:00"…</summary>
    public string ScheduleText() => Every switch
    {
        HealthInterval.Day => $"every day at {Time}",
        HealthInterval.Week => $"every {Day} at {Time}",
        HealthInterval.Month => $"on the {Ordinal(DayOfMonth)} of each month at {Time}",
        _ => "every hour",
    };
}

/// <summary>What the background check remembers about one drive between runs.</summary>
public sealed class DriveState
{
    public string Name { get; set; } = "";
    public HealthStatus Status { get; set; }
    public int? Life { get; set; }
    public int? Temperature { get; set; }
    /// <summary>Bad-sector count the next increase is measured from.</summary>
    public long SectorBaseline { get; set; }
    public bool LowLifeAlerted { get; set; }
    public bool HotAlerted { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class HealthState
{
    public DateTime? LastCheckUtc { get; set; }
    public Dictionary<string, DriveState> Drives { get; set; } = new();
}

public sealed record HealthAlert(DriveHealth Drive, string Title, string Detail, bool Serious);

/// <summary>
/// The background disk health check (DiskImager.exe --health-check, run by Task Scheduler as SYSTEM):
/// reads every drive, compares with the last check and emails once per crossed limit.
/// </summary>
public static class HealthMonitor
{
    /// <summary>Temperatures must drop this far below the limit before a new "too hot" alert can be sent.</summary>
    public const int TemperatureHysteresis = 3;

    private static string StatePath => Path.Combine(JobStore.DataFolder, "health-state.json");

    public static HealthState LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<HealthState>(File.ReadAllText(StatePath), JobStore.Json) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveState(HealthState state)
    {
        Directory.CreateDirectory(JobStore.DataFolder);
        string tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JobStore.Json));
        File.Move(tmp, StatePath, overwrite: true);
    }

    /// <summary>
    /// Compares this check with the remembered state, updates the state and returns the alerts to send.
    /// Each limit alerts once when crossed and again only after the drive has recovered. Pure logic, so it can be tested.
    /// </summary>
    public static List<HealthAlert> Compare(HealthState state, IEnumerable<DriveHealth> drives, HealthSettings s, DateTime nowUtc)
    {
        var alerts = new List<HealthAlert>();
        foreach (var d in drives)
        {
            // A drive that couldn't be read this time (asleep, unplugged, busy) keeps its last known state.
            if (d.Status == HealthStatus.Unknown) continue;

            bool isNew = !state.Drives.TryGetValue(d.Key, out var prev);
            prev ??= new DriveState { Status = HealthStatus.Unknown, SectorBaseline = d.BadSectors ?? 0 };

            if (s.EmailOnStatusChange)
            {
                if (isNew || prev.Status == HealthStatus.Unknown)
                {
                    if (d.Status is HealthStatus.Caution or HealthStatus.Bad)
                        alerts.Add(new(d, $"Health is {d.StatusText}", FirstReason(d, $"The first health check of this drive found it in {d.Status} condition."),
                            d.Status == HealthStatus.Bad));
                }
                else if (d.Status != prev.Status)
                {
                    bool worse = d.Status > prev.Status;
                    alerts.Add(new(d, $"Health changed from {prev.Status} to {d.Status}",
                        worse ? FirstReason(d, "The drive's health got worse.") : "The drive's health readings have improved.",
                        d.Status == HealthStatus.Bad));
                }
            }

            if (d.LifePercent is int life)
            {
                if (life < s.LowLifePercent)
                {
                    if (!prev.LowLifeAlerted && s.EmailOnLowLife)
                        alerts.Add(new(d, $"Health dropped below {s.LowLifePercent}%",
                            $"Only {life}% of the drive's rated life is left (your alert limit is {s.LowLifePercent}%). Plan to replace it.", life <= 10));
                    prev.LowLifeAlerted = true;
                }
                else prev.LowLifeAlerted = false;
            }

            if (d.TemperatureC is int t)
            {
                int limit = d.Kind == DriveKind.Hdd ? s.HotHddC : s.HotSsdC;
                if (t > limit)
                {
                    if (!prev.HotAlerted && s.EmailOnHot)
                        alerts.Add(new(d, $"Temperature is {t} °C",
                            $"The drive is at {t} °C, above your limit of {limit} °C. Check the PC's cooling and airflow.", false));
                    prev.HotAlerted = true;
                }
                else if (t <= limit - TemperatureHysteresis) prev.HotAlerted = false;
            }

            if (d.BadSectors is long n)
            {
                long increase = n - prev.SectorBaseline;
                if (increase >= Math.Max(1, s.BadSectorIncrease))
                {
                    if (s.EmailOnBadSectors && !isNew)
                        alerts.Add(new(d, $"{increase} new bad sector{(increase == 1 ? "" : "s")}",
                            d.Kind == DriveKind.Nvme
                                ? $"Media and data integrity errors went from {prev.SectorBaseline} to {n}."
                                : $"Reallocated, pending and uncorrectable sectors went from {prev.SectorBaseline} to {n}.", true));
                    prev.SectorBaseline = n;
                }
                else if (n < prev.SectorBaseline) prev.SectorBaseline = n;
            }

            prev.Name = d.Name;
            prev.Status = d.Status;
            prev.Life = d.LifePercent;
            prev.Temperature = d.TemperatureC;
            prev.LastSeenUtc = nowUtc;
            state.Drives[d.Key] = prev;
        }
        state.LastCheckUtc = nowUtc;
        return alerts;
    }

    private static string FirstReason(DriveHealth d, string fallback) => d.Reasons.Count > 0 ? string.Join(" ", d.Reasons) : fallback;

    /// <summary>The scheduled check. Never throws; returns the process exit code.</summary>
    public static int Run(Logger log)
    {
        try
        {
            var settings = AppSettings.Load();
            var drives = SmartReader.ReadAll(log);
            var state = LoadState();
            var alerts = Compare(state, drives, settings.Health, DateTime.UtcNow);
            SaveState(state);
            log.Info("Disk health check: " + string.Join("; ", drives.Select(d => $"Disk {d.DiskNumber} {d.Model}: {d.StatusText}" +
                (d.TemperatureC is int t ? $", {t} °C" : ""))));
            if (alerts.Count == 0) return 0;

            foreach (var a in alerts) log.Warn($"Disk health alert – {a.Drive.Name}: {a.Title}. {a.Detail}");
            NotifyTray(alerts);
            var email = settings.Email;
            if (string.IsNullOrWhiteSpace(email.Host) || string.IsNullOrWhiteSpace(email.To))
            {
                log.Warn("Disk health alert not emailed: no email server or address is set in Settings.");
                return 0;
            }
            try
            {
                Notifier.Send(email, Compose(alerts, settings.Report));
                log.Info($"Disk health alert emailed to {email.To}.");
            }
            catch (Exception ex) { log.Warn("Could not email the disk health alert: " + ex.Message); }
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Disk health check failed: " + ex.Message);
            return 1;
        }
    }

    public static EmailMessage Compose(List<HealthAlert> alerts, ReportSettings labels)
    {
        bool serious = alerts.Any(a => a.Serious || a.Drive.Status == HealthStatus.Bad);
        var drives = alerts.Select(a => a.Drive).DistinctBy(d => d.Key).ToList();
        string what = drives.Count == 1 ? $"{drives[0].Model} ({alerts[0].Title})" : $"{drives.Count} drives need attention";
        var m = new EmailMessage
        {
            Subject = $"{WeeklyReport.Prefix(labels)} {(serious ? "❌" : "⚠️")} Drive health alert: {what} on {Environment.MachineName}",
            Tone = serious ? EmailTone.Failure : EmailTone.Warning,
            StatusLabel = serious ? "Act now" : "Check soon",
            Heading = "Drive health alert",
            Intro = drives.Count == 1
                ? $"DiskImager's background check found a change on a drive in {Environment.MachineName}."
                : $"DiskImager's background check found changes on {drives.Count} drives in {Environment.MachineName}.",
        };
        if (labels.ClientName.Length > 0) m.Rows.Add(("Client", labels.ClientName));
        m.Rows.Add(("Computer", labels.Location.Length > 0 ? $"{Environment.MachineName} ({labels.Location})" : Environment.MachineName));
        foreach (var d in drives)
        {
            string facts = string.Join(" · ", new[]
            {
                d.StatusText,
                d.TemperatureC is int t ? $"{t} °C" : "",
                d.KindText,
                Util.FormatBytes(d.Size),
                d.Serial.Length > 0 ? "S/N " + d.Serial : "",
                d.DriveLetters,
            }.Where(x => x.Length > 0));
            m.Rows.Add(($"Disk {d.DiskNumber}", $"{d.Model} – {facts}"));
            foreach (var a in alerts.Where(a => a.Drive.Key == d.Key))
                m.Rows.Add(("   " + a.Title, a.Detail));
        }
        m.Rows.Add(("Checked", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        if (serious)
            m.Notes.Add("Make sure this drive is backed up now, and plan to replace it. A drive in Bad condition can stop working at any time.");
        else
            m.Notes.Add("No action is needed straight away, but keep an eye on this drive and make sure its backups are up to date.");
        m.Notes.Add("Open DiskImager → Disk health for all of the drive's readings. Alert limits are under Settings → Disk health monitoring.");
        return m;
    }

    /// <summary>Leaves a note for the tray helper in each signed-in session to show as a notification.</summary>
    private static void NotifyTray(List<HealthAlert> alerts)
    {
        try
        {
            AgentChannel.EnsureFolder();
            foreach (var old in Directory.EnumerateFiles(AgentChannel.Folder, "health-*.json"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromDays(1)) File.Delete(old);
            var first = alerts[0];
            string title = alerts.Count == 1 ? $"{first.Drive.Model}: {first.Title}" : $"{alerts.Count} drive health alerts";
            string text = alerts.Count == 1 ? first.Detail : string.Join("\n", alerts.Select(a => $"{a.Drive.Model}: {a.Title}"));
            string path = Path.Combine(AgentChannel.Folder, $"health-{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new TrayNote { Title = title, Text = text, Serious = alerts.Any(a => a.Serious) }));
        }
        catch { }
    }

    public sealed class TrayNote
    {
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Serious { get; set; }
    }
}
