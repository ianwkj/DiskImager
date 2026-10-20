using System.Net;
using System.Text;
using System.Text.Json;

namespace DiskImager.Core;

/// <summary>This PC's labels and the weekly office report settings (stored in settings.json).</summary>
public sealed class ReportSettings
{
    /// <summary>Which client this PC belongs to, e.g. "ACME Pte Ltd" (shown in every email subject).</summary>
    public string ClientName { get; set; } = "";
    /// <summary>Where the PC is or what it's for, e.g. "Front desk".</summary>
    public string Location { get; set; } = "";
    /// <summary>This PC may send the weekly report. Several PCs can have this on: the first one that's on after
    /// the due time sends it, the others skip (see <see cref="WeeklyReport.SendScheduled"/>).</summary>
    public bool SendWeekly { get; set; }
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public string Time { get; set; } = "08:00";
    /// <summary>When the report settings were last saved: only due times after this count (no surprise report on saving).</summary>
    public DateTime? SendingSinceUtc { get; set; }
    /// <summary>The latest due time this PC has dealt with (sent it, or saw another PC had), so hourly checks stay local.</summary>
    public DateTime? LastHandledDueUtc { get; set; }
    /// <summary>Stable ID for this PC's status note (survives renaming the PC).</summary>
    public Guid MachineId { get; set; }
}

public sealed class JobStatus
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public DateTime? LastRunUtc { get; set; }
    public string? LastResult { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    /// <summary>Longest normal gap between this job's scheduled runs, in hours (0 = not scheduled).</summary>
    public double ExpectedEveryHours { get; set; }
    public long? LastImageBytes { get; set; }
    /// <summary>The job's schedules and usual run times, so other PCs can pick times that don't clash (1.4.0+).</summary>
    public Schedule? Full { get; set; }
    public Schedule? Incremental { get; set; }
    public double? FullMinutes { get; set; }
    public double? IncrementalMinutes { get; set; }
}

/// <summary>A PC's status note, left next to its backups after every run.</summary>
public sealed class PcStatus
{
    public Guid MachineId { get; set; }
    public string ComputerName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Location { get; set; } = "";
    public string? LastUser { get; set; }
    public string Version { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public List<JobStatus> Jobs { get; set; } = new();
}

public enum PcHealth { Ok, Failing, Late, Silent }

/// <summary>
/// The weekly health report. Every PC writes a status note into a "_DiskImagerStatus" folder at the root of
/// each place it backs up to (the NAS share, or the backup drive). The PC chosen to send the report reads all
/// notes in its destinations and emails one summary per client, so a PC that stops backing up, or stops
/// reporting altogether (switched off, broken), shows up.
/// </summary>
public static class WeeklyReport
{
    public const string FolderName = "_DiskImagerStatus";
    private static readonly JsonSerializerOptions Json = new(JobStore.Json) { WriteIndented = true };

    public static string Prefix(ReportSettings r) => r.ClientName.Trim().Length > 0 ? $"[{r.ClientName.Trim()}]" : "[DiskImager]";

    /// <summary>\\NAS\Backups\PC1 → \\NAS\Backups\_DiskImagerStatus;  E:\Backups\PC1 → E:\_DiskImagerStatus</summary>
    public static string StatusFolder(string destination)
    {
        // Tests: a stand-in for the share's status folder (so tests never write to a real share or drive root).
        if (TestStatusFolder is { } test) return test;
        return Path.Combine(LocationRoot(destination) + "\\", FolderName);
    }

    /// <summary>The share (\\NAS\Backups) or drive (E:) a destination is in: where its status folder lives.</summary>
    public static string LocationRoot(string destination) =>
        NetworkShare.IsNetworkPath(destination) ? NetworkShare.ShareRoot(destination) : Path.GetPathRoot(destination)!.TrimEnd('\\');

    public static string? TestStatusFolder =>
        Environment.GetEnvironmentVariable("DISKIMAGER_TEST_STATUSFOLDER") is { Length: > 0 } test ? test : null;

    public static ReportSettings EnsureMachineId()
    {
        var s = AppSettings.Load();
        if (s.Report.MachineId == Guid.Empty)
        {
            s.Report.MachineId = Guid.NewGuid();
            try { s.Save(); } catch { }
        }
        return s.Report;
    }

    /// <summary>Longest normal gap between runs of a schedule, in hours.</summary>
    public static double Interval(Schedule s) => s.Kind switch
    {
        ScheduleKind.Daily => 24,
        ScheduleKind.Weekly => s.Days.Count <= 1 ? 168 : MaxWeeklyGap(s.Days),
        ScheduleKind.Monthly => 31 * 24,
        _ => 0,
    };

    private static double MaxWeeklyGap(List<DayOfWeek> days)
    {
        var d = days.Select(x => (int)x).Distinct().OrderBy(x => x).ToList();
        int max = 0;
        for (int i = 0; i < d.Count; i++)
        {
            int gap = (d[(i + 1) % d.Count] - d[i] + 7) % 7;
            max = Math.Max(max, gap == 0 ? 7 : gap);
        }
        return max * 24;
    }

    public static double ExpectedEvery(BackupJob j)
    {
        var gaps = new[] { Interval(j.Full), Interval(j.Incremental) }.Where(g => g > 0).ToList();
        return gaps.Count == 0 ? 0 : gaps.Min();
    }

    /// <summary>OK, failing (last run failed), late (no success for longer than its schedule allows) or silent (no note).</summary>
    public static PcHealth Health(PcStatus pc, DateTime nowUtc)
    {
        double expected = pc.Jobs.Where(j => j.ExpectedEveryHours > 0).Select(j => j.ExpectedEveryHours).DefaultIfEmpty(168).Min();
        double grace = expected + 24;
        if ((nowUtc - pc.UpdatedUtc).TotalHours > grace + 24) return PcHealth.Silent;
        foreach (var j in pc.Jobs.Where(j => j.ExpectedEveryHours > 0))
        {
            if (j.LastResult != null && j.LastResult.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase)) return PcHealth.Failing;
            if (j.LastSuccessUtc == null || (nowUtc - j.LastSuccessUtc.Value).TotalHours > j.ExpectedEveryHours + 24) return PcHealth.Late;
        }
        return PcHealth.Ok;
    }

    /// <summary>Who uses this PC: the person signed in now, or else the last one who signed in.</summary>
    public static string? CurrentUser()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject o in searcher.Get())
                if (o["UserName"] is string u && u.Length > 0) return Short(u);
        }
        catch { }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI");
            if (k?.GetValue("LastLoggedOnUser") is string last && last.Length > 0) return Short(last);
        }
        catch { }
        return null;
        static string Short(string u) => u.Contains('\\') ? u[(u.LastIndexOf('\\') + 1)..] : u;
    }

    /// <summary>Writes this PC's status note into every destination its scheduled jobs use. Never throws.</summary>
    public static void WriteStatusNotes(Logger log)
    {
        try
        {
            var settings = EnsureMachineId();
            var jobs = JobStore.Load().Where(j => j.RegisteredTasks.Count > 0 || j.LastRunUtc != null).ToList();
            string version = typeof(WeeklyReport).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
            string? user = CurrentUser();
            foreach (var group in jobs.Where(j => j.Destination.Length > 2).GroupBy(j => StatusFolder(j.Destination), StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var first = group.First();
                    NetworkShare.Connect(first.Destination, first.NetUser, JobStore.Unprotect(first.NetPasswordProtected), null);
                    Directory.CreateDirectory(group.Key);
                    string file = Path.Combine(group.Key, settings.MachineId + ".json");
                    var previous = TryRead(file);
                    var note = new PcStatus
                    {
                        MachineId = settings.MachineId,
                        ComputerName = Environment.MachineName,
                        ClientName = settings.ClientName,
                        Location = settings.Location,
                        LastUser = user ?? previous?.LastUser,
                        Version = version,
                        UpdatedUtc = DateTime.UtcNow,
                        Jobs = group.Select(j => new JobStatus
                        {
                            Name = j.Name,
                            Destination = j.Destination,
                            LastRunUtc = j.LastRunUtc,
                            LastResult = j.LastResult,
                            LastSuccessUtc = j.LastSuccessUtc,
                            ExpectedEveryHours = ExpectedEvery(j),
                            LastImageBytes = LatestImageSize(j),
                            Full = j.RegisteredTasks.Count > 0 ? j.Full : null,
                            Incremental = j.RegisteredTasks.Count > 0 ? j.Incremental : null,
                            FullMinutes = j.LastFullMinutes,
                            IncrementalMinutes = j.LastIncrementalMinutes,
                        }).ToList(),
                    };
                    string tmp = file + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(note, Json));
                    File.Move(tmp, file, overwrite: true);
                }
                catch (Exception ex) { log.Warn($"Couldn't leave a status note in {group.Key}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { log.Warn("Couldn't write status notes: " + ex.Message); }
    }

    /// <summary>Takes a deleted (or moved) job off this PC's note at its old location, so other PCs stop seeing its times.</summary>
    public static void ForgetJob(BackupJob job, Logger log)
    {
        try
        {
            if (job.Destination.Length <= 2) return;
            var settings = EnsureMachineId();
            NetworkShare.Connect(job.Destination, job.NetUser, JobStore.Unprotect(job.NetPasswordProtected), null);
            string file = Path.Combine(StatusFolder(job.Destination), settings.MachineId + ".json");
            if (TryRead(file) is not { } note) return;
            if (note.Jobs.RemoveAll(j => string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase)) == 0) return;
            note.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(note, Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) { log.Warn($"Couldn't update the status note for '{job.Name}': {ex.Message}"); }
    }

    private static long? LatestImageSize(BackupJob j)
    {
        try
        {
            return ImageCatalog.Scan(j.Destination).Where(i => string.Equals(i.Header.JobName, j.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Header.CreatedUtc).FirstOrDefault()?.FileSize;
        }
        catch { return null; }
    }

    private static PcStatus? TryRead(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<PcStatus>(File.ReadAllText(file), Json) : null; }
        catch { return null; }
    }

    public sealed class Space
    {
        public string Folder { get; init; } = "";
        public long Total { get; init; }
        public long Free { get; init; }
        public double? WeeksLeft { get; init; }
    }

    public sealed class ClientReport
    {
        public string Client { get; init; } = "";
        public List<PcStatus> Pcs { get; init; } = new();
        public List<Space> Spaces { get; init; } = new();
        public string Subject { get; set; } = "";
        public string Html { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>Reads every status note this PC can reach (its jobs' destinations) and builds one report per client.</summary>
    public static List<ClientReport> Build(Logger log, DateTime nowUtc) => Build(ReachableStatusFolders(log), nowUtc);

    /// <summary>Builds the reports from the status notes in the given folders.</summary>
    public static List<ClientReport> Build(IEnumerable<string> statusFolders, DateTime nowUtc)
    {
        var notes = new Dictionary<Guid, (PcStatus Note, string Folder)>();
        var spaces = new List<Space>();
        foreach (var folder in statusFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (Path.GetFileName(f).StartsWith("_")) continue;
                var n = TryRead(f);
                if (n == null) continue;
                if (!notes.TryGetValue(n.MachineId, out var known) || known.Note.UpdatedUtc < n.UpdatedUtc) notes[n.MachineId] = (n, folder);
            }
            spaces.Add(MeasureSpace(folder, nowUtc));
        }

        var reports = new List<ClientReport>();
        foreach (var client in notes.Values.GroupBy(n => n.Note.ClientName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var folders = client.Select(c => c.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var r = new ClientReport
            {
                Client = client.Key,
                Pcs = client.Select(c => c.Note).OrderBy(p => p.ComputerName, StringComparer.OrdinalIgnoreCase).ToList(),
                Spaces = spaces.Where(s => folders.Contains(s.Folder, StringComparer.OrdinalIgnoreCase)).ToList(),
            };
            Render(r, nowUtc);
            reports.Add(r);
        }
        return reports;
    }

    /// <summary>Free space at a status folder, plus a "weeks until full" estimate from the space history kept there.</summary>
    private static Space MeasureSpace(string folder, DateTime nowUtc)
    {
        if (!Native.GetDiskFreeSpaceExW(folder.TrimEnd('\\') + "\\", out ulong avail, out ulong total, out _))
            return new Space { Folder = folder };
        long used = (long)(total - avail);
        string historyFile = Path.Combine(folder, "_space-history.json");
        var history = new List<(DateTime Utc, long Used)>();
        try
        {
            if (File.Exists(historyFile))
                history = JsonSerializer.Deserialize<List<long[]>>(File.ReadAllText(historyFile))!
                    .Select(x => (new DateTime(x[0], DateTimeKind.Utc), x[1])).ToList();
        }
        catch { }
        history.Add((nowUtc, used));
        history = history.Where(h => (nowUtc - h.Utc).TotalDays <= 70).ToList();
        try { File.WriteAllText(historyFile, JsonSerializer.Serialize(history.Select(h => new[] { h.Utc.Ticks, h.Used }))); } catch { }

        double? weeks = null;
        var oldest = history.OrderBy(h => h.Utc).First();
        double spanWeeks = (nowUtc - oldest.Utc).TotalDays / 7;
        if (spanWeeks >= 0.9)
        {
            double perWeek = (used - oldest.Used) / spanWeeks;
            if (perWeek > 0) weeks = (double)avail / perWeek;
        }
        return new Space { Folder = folder, Total = (long)total, Free = (long)avail, WeeksLeft = weeks };
    }

    // ------------------------------------------------------------------ email

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string When(DateTime? utc, DateTime nowUtc)
    {
        if (utc == null) return "never";
        var local = utc.Value.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        if (local.Date == today) return $"Today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.ToString("d MMM, HH:mm");
    }

    public static void Render(ClientReport r, DateTime nowUtc)
    {
        var health = r.Pcs.ToDictionary(p => p.MachineId, p => Health(p, nowUtc));
        int ok = health.Values.Count(h => h == PcHealth.Ok), total = r.Pcs.Count;
        var bad = r.Pcs.Where(p => health[p.MachineId] != PcHealth.Ok).ToList();
        string client = r.Client.Length > 0 ? r.Client : "Backups";
        string prefix = r.Client.Length > 0 ? $"[{r.Client}]" : "[DiskImager]";
        var start = nowUtc.ToLocalTime().AddDays(-6).Date;
        r.Subject = $"{prefix} Weekly backup report: {ok} of {total} PC{(total == 1 ? "" : "s")} OK";

        string Problem(PcStatus p)
        {
            var last = p.Jobs.Select(j => j.LastSuccessUtc).Where(t => t != null).DefaultIfEmpty().Max();
            string since = last == null ? "has never completed a backup" : $"hasn't backed up since {When(last, nowUtc)}";
            return health[p.MachineId] switch
            {
                PcHealth.Silent => $"{p.ComputerName} hasn't reported in since {When(p.UpdatedUtc, nowUtc)}: it may be switched off, broken, or unable to reach the backup location.",
                PcHealth.Failing => $"{p.ComputerName}: the last backup failed ({p.Jobs.First(j => j.LastResult?.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase) == true).LastResult!.Split('–', 2).Last().Trim()}).",
                _ => $"{p.ComputerName} {since}. Most often the PC was off at backup time.",
            };
        }

        string Pill(PcHealth h, PcStatus p) => h switch
        {
            PcHealth.Ok => "<span style=\"background:#16A34A;color:#fff;border-radius:999px;padding:2px 10px;font-size:12px;\">OK</span>",
            PcHealth.Failing => "<span style=\"background:#DC2626;color:#fff;border-radius:999px;padding:2px 10px;font-size:12px;\">Failed</span>",
            PcHealth.Silent => "<span style=\"background:#D97706;color:#fff;border-radius:999px;padding:2px 10px;font-size:12px;\">Silent</span>",
            _ => $"<span style=\"background:#DC2626;color:#fff;border-radius:999px;padding:2px 10px;font-size:12px;\">{DaysSince(p, nowUtc)}</span>",
        };

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>");
        sb.Append("<body style=\"margin:0;padding:0;background:#F1F5F9;font-family:'Segoe UI',Helvetica,Arial,sans-serif;color:#0F172A;\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#F1F5F9;padding:28px 12px;\"><tr><td align=\"center\">");
        sb.Append("<table role=\"presentation\" width=\"620\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:620px;width:100%;background:#FFFFFF;border:1px solid #E2E8F0;border-radius:14px;overflow:hidden;\">");
        sb.Append($"<tr><td style=\"background:{(bad.Count == 0 ? "#16A34A" : "#2563EB")};padding:22px 28px;color:#FFFFFF;\">");
        sb.Append($"<div style=\"font-size:12px;letter-spacing:1.5px;text-transform:uppercase;opacity:0.85;\">DiskImager · weekly report · {E(client)}</div>");
        sb.Append($"<div style=\"font-size:22px;font-weight:600;margin-top:6px;\">Backups this week: {ok} of {total} PC{(total == 1 ? "" : "s")} OK</div>");
        sb.Append($"<div style=\"font-size:13px;opacity:0.9;margin-top:4px;\">{start:d MMM} – {nowUtc.ToLocalTime():d MMM yyyy}</div></td></tr>");
        sb.Append("<tr><td style=\"padding:22px 28px;\">");

        foreach (var p in bad)
        {
            sb.Append("<div style=\"margin-bottom:12px;padding:12px 16px;background:#FEF2F2;border:1px solid #FECACA;border-radius:10px;font-size:14px;line-height:1.5;color:#991B1B;\">");
            sb.Append(E(Problem(p))).Append("</div>");
        }

        sb.Append("<div style=\"margin-top:10px;font-size:11px;font-weight:700;letter-spacing:1px;color:#64748B;\">EACH PC</div>");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:13.5px;margin-top:6px;\">");
        sb.Append("<tr style=\"color:#64748B;font-size:12px;\"><td style=\"padding:8px 0;border-bottom:1px solid #E2E8F0;\">PC</td><td style=\"padding:8px 0;border-bottom:1px solid #E2E8F0;\">Last good backup</td><td style=\"padding:8px 0;border-bottom:1px solid #E2E8F0;\">Size</td><td style=\"padding:8px 0;border-bottom:1px solid #E2E8F0;\">Status</td></tr>");
        foreach (var p in r.Pcs)
        {
            var h = health[p.MachineId];
            var last = p.Jobs.Select(j => j.LastSuccessUtc).Where(t => t != null).DefaultIfEmpty().Max();
            long? size = p.Jobs.Where(j => j.LastImageBytes != null).Select(j => j.LastImageBytes).DefaultIfEmpty().Max();
            string who = string.Join(" · ", new[] { p.Location, p.LastUser != null ? "used by " + p.LastUser : null }.Where(s => !string.IsNullOrEmpty(s)));
            sb.Append("<tr><td style=\"padding:9px 0;border-bottom:1px solid #EEF2F6;\">");
            sb.Append($"<div style=\"font-weight:600;\">{E(p.ComputerName)}</div>");
            if (who.Length > 0) sb.Append($"<div style=\"font-size:12px;color:#64748B;\">{E(who)}</div>");
            sb.Append("</td>");
            sb.Append($"<td style=\"padding:9px 0;border-bottom:1px solid #EEF2F6;{(h == PcHealth.Ok ? "" : "color:#991B1B;")}\">{E(When(last, nowUtc))}</td>");
            sb.Append($"<td style=\"padding:9px 0;border-bottom:1px solid #EEF2F6;\">{(size is long s ? Util.FormatBytes(s) : "–")}</td>");
            sb.Append($"<td style=\"padding:9px 0;border-bottom:1px solid #EEF2F6;\">{Pill(h, p)}</td></tr>");
        }
        sb.Append("</table>");

        foreach (var s in r.Spaces.Where(s => s.Total > 0))
        {
            double pct = 100.0 * (s.Total - s.Free) / s.Total;
            string color = pct >= 90 ? "#DC2626" : pct >= 75 ? "#D97706" : "#2563EB";
            sb.Append($"<div style=\"margin-top:22px;font-size:11px;font-weight:700;letter-spacing:1px;color:#64748B;\">BACKUP SPACE · {E(Path.GetDirectoryName(s.Folder) ?? s.Folder)}</div>");
            sb.Append("<div style=\"margin-top:8px;height:10px;background:#E2E8F0;border-radius:999px;overflow:hidden;\">");
            sb.Append($"<div style=\"width:{pct:0}%;height:10px;background:{color};\"></div></div>");
            string trend = s.WeeksLeft is double w ? (w < 1 ? " · at this rate, full within a week" : $" · at this rate, full in about {Math.Round(w):0} week{(Math.Round(w) == 1 ? "" : "s")}") : "";
            sb.Append($"<div style=\"margin-top:6px;font-size:13px;color:#475569;\">{Util.FormatBytes(s.Total - s.Free)} of {Util.FormatBytes(s.Total)} used ({pct:0}%) · {Util.FormatBytes(s.Free)} free{trend}</div>");
        }

        sb.Append("<div style=\"margin-top:20px;padding:12px 16px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;font-size:13px;line-height:1.5;color:#334155;\">");
        sb.Append("Each PC leaves a status note next to its backups after every run. A PC marked <b>Silent</b> hasn't left one recently.</div>");
        sb.Append("</td></tr>");
        sb.Append("<tr><td style=\"padding:14px 28px;background:#F8FAFC;border-top:1px solid #E2E8F0;font-size:12px;color:#64748B;\">");
        sb.Append($"Sent by DiskImager on {E(Environment.MachineName)} &middot; Powered by <a href=\"https://e-techsource.com\" style=\"color:#2563EB;font-weight:700;text-decoration:none;\">TechSource Digital</a>");
        sb.Append("</td></tr></table></td></tr></table></body></html>");
        r.Html = sb.ToString();

        var t = new StringBuilder();
        t.AppendLine($"{client.ToUpperInvariant()}: BACKUPS THIS WEEK: {ok} OF {total} PCS OK");
        t.AppendLine();
        foreach (var p in bad) t.AppendLine("! " + Problem(p));
        if (bad.Count > 0) t.AppendLine();
        foreach (var p in r.Pcs)
        {
            var last = p.Jobs.Select(j => j.LastSuccessUtc).Where(x => x != null).DefaultIfEmpty().Max();
            t.AppendLine($"{p.ComputerName,-18} {(p.Location.Length > 0 ? p.Location : ""),-16} last good: {When(last, nowUtc),-18} {health[p.MachineId]}");
        }
        foreach (var s in r.Spaces.Where(s => s.Total > 0))
            t.AppendLine($"\nBackup space at {Path.GetDirectoryName(s.Folder)}: {Util.FormatBytes(s.Total - s.Free)} of {Util.FormatBytes(s.Total)} used, {Util.FormatBytes(s.Free)} free.");
        t.AppendLine("\n--\nSent by DiskImager · Powered by TechSource Digital (https://e-techsource.com)");
        r.Text = t.ToString();
    }

    private static string DaysSince(PcStatus p, DateTime nowUtc)
    {
        var last = p.Jobs.Select(j => j.LastSuccessUtc).Where(t => t != null).DefaultIfEmpty().Max();
        if (last == null) return "No backup";
        int days = (int)(nowUtc - last.Value).TotalDays;
        return days <= 1 ? "Late" : $"{days} days";
    }

    /// <summary>"Send report now": builds and emails the reports straight away. Returns a one-line summary.</summary>
    public static string Send(Logger log)
    {
        var settings = AppSettings.Load();
        if (!settings.Email.Enabled) throw new InvalidOperationException("Email isn't set up on this PC (Settings → Email notifications).");
        var folders = ReachableStatusFolders(log);
        var reports = Build(folders, DateTime.UtcNow);
        if (reports.Count == 0) throw new InvalidOperationException("No status notes were found. Each PC leaves one next to its backups after its next backup run.");
        SendAll(settings.Email, reports, log);
        foreach (var f in folders) MarkSent(f, DateTime.UtcNow, settings.Report.MachineId); // counts as this week's report
        return $"Sent {reports.Count} report{(reports.Count == 1 ? "" : "s")} covering {reports.Sum(r => r.Pcs.Count)} PC(s).";
    }

    private static void SendAll(EmailSettings email, List<ClientReport> reports, Logger log)
    {
        foreach (var r in reports)
        {
            Notifier.Send(email, r.Subject, r.Text, r.Html);
            log.Info($"Weekly report sent: {r.Subject}");
        }
    }

    /// <summary>Writes this PC's notes, then returns the status folders of its jobs' destinations (connecting to shares).</summary>
    private static List<string> ReachableStatusFolders(Logger log)
    {
        WriteStatusNotes(log);
        var folders = new List<string>();
        foreach (var group in JobStore.Load().Where(j => j.Destination.Length > 2).GroupBy(j => StatusFolder(j.Destination), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            try { NetworkShare.Connect(first.Destination, first.NetUser, JobStore.Unprotect(first.NetPasswordProtected), null); } catch { }
            folders.Add(group.Key);
        }
        return folders;
    }

    // ------------------------------------------------------------------ shared sending
    // Any number of PCs can have "this PC can send the weekly report" ticked. After the due time each of them
    // checks (at the due time, at startup and hourly). The first to claim a status folder sends the report for it;
    // the claim is a lock file created atomically on the share, and a marker records that this week's report went out.

    private const string SentMarkerFile = "_report-sent.json";
    private const string ClaimFile = "_report-sending.lock";
    /// <summary>A claim older than this belongs to a PC that crashed or lost the network mid-send; another PC may take over.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(30);

    public sealed class SentMarker
    {
        public DateTime SentUtc { get; set; }
        public string SentBy { get; set; } = "";
        public Guid SentById { get; set; }
    }

    public enum Claim { Claimed, AlreadySent, BeingSent, Unreachable }

    /// <summary>The most recent scheduled time (local) at or before <paramref name="nowLocal"/>.</summary>
    public static DateTime LastDue(DayOfWeek day, string time, DateTime nowLocal)
    {
        if (!TimeSpan.TryParse(time, out var t)) t = new TimeSpan(8, 0, 0);
        var due = nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek - (int)day + 7) % 7)) + t;
        return due > nowLocal ? due.AddDays(-7) : due;
    }

    /// <summary>Tries to become the PC that sends this week's report for one status folder.</summary>
    public static Claim TryClaim(string folder, DateTime dueUtc, DateTime nowUtc)
    {
        if (!Directory.Exists(folder)) return Claim.Unreachable;
        string claim = Path.Combine(folder, ClaimFile);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // CreateNew is atomic, also on network shares: only one PC can create the claim.
                using (var fs = new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                    w.Write($"{Environment.MachineName} {nowUtc:O}");
                File.SetLastWriteTimeUtc(claim, nowUtc);
                if (ReadMarker(folder) is { } m && m.SentUtc >= dueUtc)
                {
                    Release(folder);
                    return Claim.AlreadySent;
                }
                return Claim.Claimed;
            }
            catch (IOException) when (File.Exists(claim))
            {
                if (ReadMarker(folder) is { } m && m.SentUtc >= dueUtc) return Claim.AlreadySent;
                if (nowUtc - File.GetLastWriteTimeUtc(claim) < ClaimTimeout) return Claim.BeingSent;
                try { File.Delete(claim); } catch { return Claim.BeingSent; } // stale: the other PC died mid-send
            }
            catch (Exception) { return Claim.Unreachable; }
        }
        return Claim.BeingSent;
    }

    public static void Release(string folder)
    {
        try { File.Delete(Path.Combine(folder, ClaimFile)); } catch { }
    }

    public static SentMarker? ReadMarker(string folder)
    {
        try
        {
            string f = Path.Combine(folder, SentMarkerFile);
            return File.Exists(f) ? JsonSerializer.Deserialize<SentMarker>(File.ReadAllText(f), Json) : null;
        }
        catch { return null; }
    }

    public static void MarkSent(string folder, DateTime nowUtc, Guid machineId)
    {
        try
        {
            string f = Path.Combine(folder, SentMarkerFile);
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(new SentMarker { SentUtc = nowUtc, SentBy = Environment.MachineName, SentById = machineId }, Json));
            File.Move(f + ".tmp", f, overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// The scheduled check (DiskImager.exe --weekly-report): sends this week's report if it's due, nobody has
    /// sent it yet, and this PC wins the claim. Cheap when there's nothing to do. Returns a one-line summary.
    /// </summary>
    public static string SendScheduled(Logger log)
    {
        var settings = AppSettings.Load();
        return SendScheduled(log, DateTime.UtcNow, settings.Report, settings.Email,
            r =>
            {
                var latest = AppSettings.Load(); // only these two fields: the window may have saved other settings meanwhile
                latest.Report.SendingSinceUtc = r.SendingSinceUtc;
                latest.Report.LastHandledDueUtc = r.LastHandledDueUtc;
                latest.Save();
            },
            ReachableStatusFolders, Build, (email, reports) => SendAll(email, reports, log));
    }

    /// <summary>The same, with settings, folders, building and sending passed in so it can be tested.</summary>
    public static string SendScheduled(Logger log, DateTime nowUtc, ReportSettings r, EmailSettings email, Action<ReportSettings> save,
        Func<Logger, List<string>> folders, Func<IEnumerable<string>, DateTime, List<ClientReport>> build,
        Action<EmailSettings, List<ClientReport>> send)
    {
        if (!r.SendWeekly) return "This PC doesn't send the weekly report.";
        if (r.SendingSinceUtc == null)
        {
            // Upgraded from a version that sent at a fixed time: this week's report may already have gone out.
            r.SendingSinceUtc = nowUtc;
            save(r);
            return "Not due yet.";
        }
        var dueLocal = LastDue(r.Day, r.Time, nowUtc.ToLocalTime());
        var dueUtc = dueLocal.ToUniversalTime();
        if (dueUtc < r.SendingSinceUtc) return "Not due yet.";
        if (r.LastHandledDueUtc is DateTime handled && handled >= dueUtc) return "This week's report has already gone out.";
        if (!email.Enabled)
        {
            log.Warn("Weekly report: this PC can't send it because email isn't set up here (Settings → Email notifications).");
            return "Email isn't set up on this PC.";
        }

        var claimed = new List<string>();
        var others = new List<string>();
        bool waiting = false;
        foreach (var f in folders(log))
        {
            switch (TryClaim(f, dueUtc, nowUtc))
            {
                case Claim.Claimed: claimed.Add(f); break;
                case Claim.AlreadySent: others.Add(ReadMarker(f)?.SentBy ?? "another PC"); break;
                case Claim.BeingSent: waiting = true; break;
            }
        }

        string result;
        if (claimed.Count > 0)
        {
            try
            {
                var reports = build(claimed, nowUtc);
                if (reports.Count > 0) send(email, reports);
                foreach (var f in claimed) MarkSent(f, nowUtc, r.MachineId);
                result = $"Sent this week's report ({reports.Count} client{(reports.Count == 1 ? "" : "s")}).";
            }
            catch (Exception ex)
            {
                log.Warn("Weekly report: couldn't send it, so another PC can try: " + ex.Message);
                return "Couldn't send the report: " + ex.Message;
            }
            finally
            {
                foreach (var f in claimed) Release(f);
            }
        }
        else if (waiting) return "Another PC is sending this week's report right now.";
        else if (others.Count > 0) result = $"This week's report was already sent by {string.Join(", ", others.Distinct())}.";
        else return "No backup location could be reached; will try again later.";

        r.LastHandledDueUtc = dueUtc;
        save(r);
        log.Info("Weekly report: " + result);
        return result;
    }
}
