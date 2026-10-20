using System.Net;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DiskImager.Core;

public enum EmailSecurity { Auto, StartTls, SslOnConnect, None }

public enum NotifyWhen { Never, OnFailure, Always }

public sealed class EmailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public EmailSecurity Security { get; set; } = EmailSecurity.Auto;
    public string? User { get; set; }
    public string? PasswordProtected { get; set; }
    public string From { get; set; } = "";
    /// <summary>One or more addresses separated by commas or semicolons.</summary>
    public string To { get; set; } = "";
}

public sealed class UpdateSettings
{
    /// <summary>Look for a new version on GitHub each time DiskImager opens.</summary>
    public bool CheckOnStartup { get; set; } = true;
    public string? LastAttemptVersion { get; set; }
    public DateTime LastAttemptUtc { get; set; }
}

/// <summary>Program-wide settings in %ProgramData%\DiskImager\settings.json (shared with scheduled runs).</summary>
public sealed class AppSettings
{
    public EmailSettings Email { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public ReportSettings Report { get; set; } = new();
    public HealthSettings Health { get; set; } = new();

    private static string FilePath => Path.Combine(JobStore.DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JobStore.Json) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(JobStore.DataFolder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JobStore.Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public enum EmailTone { Success, Failure, Warning, Info }

/// <summary>One notification, rendered both as HTML and as plain text.</summary>
public sealed class EmailMessage
{
    public string Subject { get; set; } = "";
    public EmailTone Tone { get; set; } = EmailTone.Info;
    /// <summary>Text of the status pill; null uses the tone's default (Success, Failed, Missed, Info).</summary>
    public string? StatusLabel { get; set; }
    public string Heading { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<(string Label, string Value)> Rows { get; } = new();
    public string? Error { get; set; }
    public List<string> Notes { get; } = new();
    public List<string> Log { get; } = new();

    private static string Footer => $"Sent by DiskImager on {Environment.MachineName} · Powered by TechSource Digital (https://e-techsource.com)";

    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(Heading.ToUpperInvariant());
            sb.AppendLine();
            sb.AppendLine(Intro);
            sb.AppendLine();
            int width = Rows.Count == 0 ? 0 : Rows.Max(r => r.Label.Length) + 2;
            foreach (var (label, value) in Rows) sb.AppendLine((label + ":").PadRight(width) + value);
            if (Error != null)
            {
                sb.AppendLine();
                sb.AppendLine("What went wrong:");
                sb.AppendLine(Error);
            }
            foreach (var n in Notes)
            {
                sb.AppendLine();
                sb.AppendLine(n);
            }
            if (Log.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent log:");
                foreach (var l in Log) sb.AppendLine(l);
            }
            sb.AppendLine();
            sb.AppendLine("--");
            sb.AppendLine(Footer);
            return sb.ToString();
        }
    }

    public string Html
    {
        get
        {
            static string E(string s) => WebUtility.HtmlEncode(s);
            var (accent, icon, label) = Tone switch
            {
                EmailTone.Success => ("#16A34A", "&#10004;", "Success"),
                EmailTone.Failure => ("#DC2626", "&#10006;", "Failed"),
                EmailTone.Warning => ("#D97706", "&#9888;", "Missed"),
                _ => ("#2563EB", "&#9993;", "Info"),
            };
            if (StatusLabel != null) label = StatusLabel;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>");
            sb.Append("<body style=\"margin:0;padding:0;background:#F1F5F9;font-family:'Segoe UI',Helvetica,Arial,sans-serif;color:#0F172A;\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#F1F5F9;padding:28px 12px;\"><tr><td align=\"center\">");
            sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;background:#FFFFFF;border:1px solid #E2E8F0;border-radius:14px;overflow:hidden;\">");

            // Banner
            sb.Append($"<tr><td style=\"background:{accent};padding:24px 30px;color:#FFFFFF;\">");
            sb.Append("<div style=\"font-size:12px;letter-spacing:1.5px;text-transform:uppercase;opacity:0.85;\">DiskImager</div>");
            sb.Append($"<div style=\"font-size:23px;font-weight:600;margin-top:6px;\">{icon}&nbsp; {E(Heading)}</div>");
            sb.Append("</td></tr>");

            // Body
            sb.Append("<tr><td style=\"padding:26px 30px 8px 30px;\">");
            sb.Append($"<p style=\"margin:0 0 20px 0;font-size:15px;line-height:1.55;color:#334155;\">{E(Intro)}</p>");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:14px;\">");
            sb.Append("<tr><td style=\"padding:10px 0;border-bottom:1px solid #EEF2F6;color:#64748B;width:150px;\">Status</td>");
            sb.Append($"<td style=\"padding:10px 0;border-bottom:1px solid #EEF2F6;\"><span style=\"display:inline-block;padding:3px 10px;border-radius:999px;background:{accent};color:#FFFFFF;font-size:12px;font-weight:600;\">{label}</span></td></tr>");
            foreach (var (l, v) in Rows)
            {
                sb.Append($"<tr><td style=\"padding:10px 0;border-bottom:1px solid #EEF2F6;color:#64748B;width:150px;vertical-align:top;\">{E(l)}</td>");
                sb.Append($"<td style=\"padding:10px 0;border-bottom:1px solid #EEF2F6;font-weight:600;word-break:break-word;\">{E(v)}</td></tr>");
            }
            sb.Append("</table>");

            if (Error != null)
            {
                sb.Append("<div style=\"margin-top:22px;padding:14px 16px;background:#FEF2F2;border:1px solid #FECACA;border-radius:10px;font-size:14px;line-height:1.5;color:#991B1B;\">");
                sb.Append($"<div style=\"font-weight:700;margin-bottom:4px;\">What went wrong</div>{E(Error)}</div>");
            }
            foreach (var n in Notes)
            {
                sb.Append("<div style=\"margin-top:16px;padding:13px 16px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;font-size:13.5px;line-height:1.5;color:#334155;\">");
                sb.Append($"{E(n)}</div>");
            }
            if (Log.Count > 0)
            {
                sb.Append("<div style=\"margin-top:24px;font-size:11px;font-weight:700;letter-spacing:1px;text-transform:uppercase;color:#64748B;\">Recent log</div>");
                sb.Append("<pre style=\"margin:8px 0 0 0;padding:14px 16px;background:#0F172A;color:#E2E8F0;border-radius:10px;font-size:11.5px;line-height:1.5;white-space:pre-wrap;word-break:break-all;font-family:Consolas,Menlo,monospace;\">");
                sb.Append(E(string.Join("\n", Log)));
                sb.Append("</pre>");
            }
            sb.Append("<div style=\"height:22px;\"></div></td></tr>");

            // Footer
            sb.Append("<tr><td style=\"padding:16px 30px;background:#F8FAFC;border-top:1px solid #E2E8F0;font-size:12px;color:#64748B;\">");
            sb.Append($"Sent by DiskImager on {E(Environment.MachineName)} &middot; Powered by " +
                      "<a href=\"https://e-techsource.com\" style=\"color:#2563EB;font-weight:700;text-decoration:none;\">TechSource Digital</a>");
            sb.Append("</td></tr></table></td></tr></table></body></html>");
            return sb.ToString();
        }
    }
}

public static class Notifier
{
    /// <summary>Sends one email (HTML with a plain-text alternative); throws with the server's message if it fails.</summary>
    public static void Send(EmailSettings s, EmailMessage m) => Send(s, m.Subject, m.Text, m.Html);

    public static void Send(EmailSettings s, string subject, string text, string html)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new InvalidOperationException("No SMTP server is set.");
        var to = s.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (to.Length == 0) throw new InvalidOperationException("No recipient address is set.");

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(s.From.Length > 0 ? s.From : s.User ?? ""));
        foreach (var addr in to) msg.To.Add(MailboxAddress.Parse(addr));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30000 };
        var security = s.Security switch
        {
            EmailSecurity.StartTls => SecureSocketOptions.StartTls,
            EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            EmailSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        client.Connect(s.Host, s.Port, security);
        if (!string.IsNullOrEmpty(s.User))
            client.Authenticate(s.User, JobStore.Unprotect(s.PasswordProtected) ?? "");
        client.Send(msg);
        client.Disconnect(true);
    }

    public static EmailMessage TestMessage()
    {
        var m = new EmailMessage
        {
            Subject = $"{WeeklyReport.Prefix(AppSettings.Load().Report)} Test email from {Environment.MachineName}",
            Tone = EmailTone.Info,
            Heading = "Test email",
            Intro = "If you can read this, DiskImager can reach you. Backup results and missed-backup alerts will arrive like this.",
        };
        m.Rows.Add(("Computer", Environment.MachineName));
        m.Rows.Add(("Sent", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        return m;
    }

    /// <summary>Emails the outcome of a job run if the job asks for it. Never throws.</summary>
    public static void JobFinished(BackupJob job, bool incremental, BackupResult? result, string? error,
        DateTime startedLocal, Logger log)
    {
        if (job.Notify == NotifyWhen.Never || (error == null && job.Notify == NotifyWhen.OnFailure)) return;
        SendQuietly(Compose(job, incremental, result, error, startedLocal, log), log);
    }

    /// <summary>Emails that a scheduled run didn't happen. Never throws. Returns true if it was sent.</summary>
    public static bool JobMissed(BackupJob job, DateTime scheduledLocal, int missedCount, Logger log)
    {
        if (job.Notify == NotifyWhen.Never) return false;
        return SendQuietly(ComposeMissed(job, scheduledLocal, missedCount), log);
    }

    private static bool SendQuietly(EmailMessage m, Logger log)
    {
        var settings = AppSettings.Load().Email;
        if (!settings.Enabled) return false;
        try
        {
            Send(settings, m);
            log.Info($"Email notification sent to {settings.To}.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not send the email notification: {ex.Message}");
            return false;
        }
    }

    public static EmailMessage Compose(BackupJob job, bool incremental, BackupResult? result, string? error,
        DateTime startedLocal, Logger log)
    {
        bool ok = error == null;
        string kind = result != null ? (result.WasFull ? "Full" : "Incremental") : (incremental ? "Incremental" : "Full");
        var labels = AppSettings.Load().Report;
        string prefix = WeeklyReport.Prefix(labels);
        var m = new EmailMessage
        {
            Subject = ok
                ? $"{prefix} ✅ Backup succeeded: {job.Name} on {Environment.MachineName}"
                : $"{prefix} ❌ BACKUP FAILED: {job.Name} on {Environment.MachineName}",
            Tone = ok ? EmailTone.Success : EmailTone.Failure,
            Heading = ok ? "Backup completed" : "Backup failed",
            Intro = ok
                ? $"The {kind.ToLowerInvariant()} backup \"{job.Name}\" finished successfully."
                : $"The {kind.ToLowerInvariant()} backup \"{job.Name}\" did not complete. Your previous backups are not affected.",
        };
        m.Rows.Add(("Job", job.Name));
        if (labels.ClientName.Length > 0) m.Rows.Add(("Client", labels.ClientName));
        m.Rows.Add(("Computer", labels.Location.Length > 0 ? $"{Environment.MachineName} ({labels.Location})" : Environment.MachineName));
        m.Rows.Add(("Backup type", kind));
        m.Rows.Add(("Source disk", job.SourceDiskDescription));
        m.Rows.Add(("Destination", job.Destination));
        m.Rows.Add(("Started", startedLocal.ToString("yyyy-MM-dd HH:mm:ss")));
        m.Rows.Add(("Finished", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        if (result != null)
        {
            m.Rows.Add(("Duration", Util.FormatDuration(result.Duration)));
            m.Rows.Add(("Data read", Util.FormatBytes(result.BytesRead)));
            m.Rows.Add(("Image size", Util.FormatBytes(result.BytesWritten)));
            m.Rows.Add(("Image file", result.ImagePath));
            if (result.Verified) m.Rows.Add(("Verified", "Yes – every block checked"));
            if (result.SpaceWarning != null) m.Notes.Add("⚠ " + result.SpaceWarning + " Free up space there soon, or lower \"Keep the last … full backups\".");
        }
        if (!ok)
        {
            m.Error = error;
            m.Log.AddRange(RecentLog(log, 40));
        }
        if (job.After != PostAction.None)
            m.Notes.Add($"After the backup, the computer was set to: {Describe(job.After)}.");
        return m;
    }

    public static EmailMessage ComposeMissed(BackupJob job, DateTime scheduledLocal, int missedCount)
    {
        var labels = AppSettings.Load().Report;
        var m = new EmailMessage
        {
            Subject = $"{WeeklyReport.Prefix(labels)} ⚠️ Backup missed: {job.Name} on {Environment.MachineName}",
            Tone = EmailTone.Warning,
            Heading = "Backup missed",
            Intro = $"The scheduled backup \"{job.Name}\" did not run on {scheduledLocal:dddd d MMMM} at {scheduledLocal:HH:mm}.",
        };
        m.Rows.Add(("Job", job.Name));
        if (labels.ClientName.Length > 0) m.Rows.Add(("Client", labels.ClientName));
        m.Rows.Add(("Computer", labels.Location.Length > 0 ? $"{Environment.MachineName} ({labels.Location})" : Environment.MachineName));
        m.Rows.Add(("Was due", scheduledLocal.ToString("yyyy-MM-dd HH:mm")));
        if (missedCount > 1) m.Rows.Add(("Runs missed", $"{missedCount} in a row"));
        m.Rows.Add(("Last successful backup", job.LastSuccessUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "None yet"));
        m.Rows.Add(("Schedule", $"Full: {job.Full.Describe()}; Incremental: {job.Incremental.Describe()}"));
        m.Rows.Add(("Destination", job.Destination));
        m.Notes.Add("Usually this means the computer was turned off (or lost power) at that time. " +
                    "Windows normally runs the missed backup shortly after the computer is back on; you'll get a separate email with its result.");
        m.Notes.Add("If you keep getting this, check the computer is left on (or asleep, not shut down) at backup time, " +
                    "and that the job still exists under Scheduled backups in DiskImager.");
        return m;
    }

    private static string Describe(PostAction a) => a switch
    {
        PostAction.Shutdown => "shut down",
        PostAction.Restart => "restart",
        _ => "do nothing",
    };

    private static IEnumerable<string> RecentLog(Logger log, int lines)
    {
        try
        {
            using var fs = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var all = new List<string>();
            while (reader.ReadLine() is { } l) all.Add(l);
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
