using System.Diagnostics;
using System.Text.Json;

namespace DiskImager.Core;

public static class RunPhase
{
    public const string Waiting = "Waiting";     // queued behind another backup
    public const string Countdown = "Countdown"; // asking the signed-in user (10 s pop-up)
    public const string Postponed = "Postponed"; // user postponed it
    public const string Running = "Running";
    public const string Finished = "Finished";
}

/// <summary>
/// What a scheduled (background) run is doing, written to %ProgramData%\DiskImager\status-{pid}.json
/// so the DiskImager window and the tray helper can show it. One file per run; it's removed when the
/// run ends (a "Finished" record is kept briefly so the tray can announce the result) and ignored if it
/// stops being refreshed (e.g. after a crash).
/// </summary>
public sealed class BackgroundStatus
{
    public string JobName { get; set; } = "";
    /// <summary>One line such as "Disk 2 – SanDisk (7.5 GB) → \\NAS\Backups".</summary>
    public string Description { get; set; } = "";
    public string Phase { get; set; } = RunPhase.Waiting;
    public string Stage { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
    public double BytesPerSecond { get; set; }
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public Guid PromptId { get; set; }
    public DateTime? CountdownEndsUtc { get; set; }
    public DateTime? PostponedUntilUtc { get; set; }
    public string? Result { get; set; }
    public bool Success { get; set; }

    public bool Waiting => Phase == RunPhase.Waiting;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FinishedShownFor = TimeSpan.FromMinutes(2);

    private static string FileFor(int pid) => Path.Combine(JobStore.DataFolder, $"status-{pid}.json");

    /// <summary>Background runs that are still alive (plus, optionally, ones that just finished), oldest first.</summary>
    public static List<BackgroundStatus> ReadAll(bool includeFinished = false)
    {
        var list = new List<BackgroundStatus>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(JobStore.DataFolder, "status-*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<BackgroundStatus>(File.ReadAllText(f));
                    if (s == null) continue;
                    var age = DateTime.UtcNow - s.UpdatedUtc;
                    if (s.Phase == RunPhase.Finished)
                    {
                        if (includeFinished && age < FinishedShownFor) list.Add(s);
                        continue;
                    }
                    if (age > StaleAfter || !IsRunning(s.ProcessId)) continue;
                    list.Add(s);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderBy(s => s.StartedUtc).ToList();
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    /// <summary>Publishes this process's status, refreshing it every 30 s so others know it's alive.</summary>
    public sealed class Publisher : IProgress<ProgressInfo>, IDisposable
    {
        private readonly BackgroundStatus _s;
        private readonly Timer _heartbeat;
        private readonly object _gate = new();
        private DateTime _lastWrite;
        private bool _finished;
        private bool _dirty;

        public Publisher(string jobName, string description = "")
        {
            CleanOldFinished();
            _s = new BackgroundStatus
            {
                JobName = jobName,
                Description = description,
                ProcessId = Environment.ProcessId,
                StartedUtc = DateTime.UtcNow,
                Phase = RunPhase.Running,
                Stage = "Starting",
            };
            Write(force: true);
            // Every 2 s: write any progress that was throttled; every 30 s at least, as a sign of life.
            _heartbeat = new Timer(_ =>
            {
                bool due;
                lock (_gate) due = _dirty || DateTime.UtcNow - _lastWrite > TimeSpan.FromSeconds(30);
                if (due) Write(force: true);
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void Set(Action<BackgroundStatus> change)
        {
            lock (_gate) change(_s);
            Write(force: true);
        }

        public void SetWaiting(bool waiting) =>
            Set(s => { s.Phase = waiting ? RunPhase.Waiting : RunPhase.Running; s.Stage = waiting ? "Waiting for another backup to finish" : "Starting"; });

        public void SetCountdown(Guid promptId, DateTime endsUtc) =>
            Set(s => { s.Phase = RunPhase.Countdown; s.PromptId = promptId; s.CountdownEndsUtc = endsUtc; s.Stage = "Starting soon"; });

        public void SetPostponed(DateTime untilUtc) =>
            Set(s => { s.Phase = RunPhase.Postponed; s.PostponedUntilUtc = untilUtc; s.Stage = $"Postponed until {untilUtc.ToLocalTime():HH:mm}"; });

        public void SetRunning() =>
            Set(s => { s.Phase = RunPhase.Running; s.Stage = "Starting"; s.CountdownEndsUtc = null; s.PostponedUntilUtc = null; });

        /// <summary>Records the outcome; the record stays for a couple of minutes so the tray can announce it.</summary>
        public void Finish(string result, bool success)
        {
            Set(s => { s.Phase = RunPhase.Finished; s.Result = result; s.Success = success; s.Stage = result; });
            _finished = true;
        }

        public void Report(ProgressInfo p)
        {
            lock (_gate)
            {
                _s.Phase = RunPhase.Running;
                _s.Stage = p.Stage;
                _s.Done = p.Done;
                _s.Total = p.Total;
                _s.BytesPerSecond = p.BytesPerSecond;
            }
            Write(force: false);
        }

        private void Write(bool force)
        {
            lock (_gate)
            {
                if (!force && DateTime.UtcNow - _lastWrite < TimeSpan.FromSeconds(1))
                {
                    _dirty = true; // the heartbeat writes it shortly
                    return;
                }
                _dirty = false;
                _lastWrite = DateTime.UtcNow;
                _s.UpdatedUtc = DateTime.UtcNow;
                try
                {
                    Directory.CreateDirectory(JobStore.DataFolder);
                    string path = FileFor(_s.ProcessId), tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_s));
                    File.Move(tmp, path, overwrite: true);
                }
                catch { }
            }
        }

        private static void CleanOldFinished()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(JobStore.DataFolder, "status-*.json"))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(f);
            }
            catch { }
        }

        public void Dispose()
        {
            _heartbeat.Dispose();
            if (_finished) return; // leave the result for the tray helper
            lock (_gate)
            {
                try { File.Delete(FileFor(_s.ProcessId)); } catch { }
            }
        }
    }
}
