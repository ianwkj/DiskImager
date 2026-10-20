using System.Text.Json;

namespace DiskImager.Core;

/// <summary>
/// Helps pick backup times that don't overlap other PCs backing up to the same place. Every PC's status note
/// (in the _DiskImagerStatus folder at the top of the share or drive) lists its jobs' schedules and how long they
/// usually take; the job editor reads them, warns about overlaps and suggests a free time.
/// </summary>
public static class ScheduleClash
{
    /// <summary>Assumed length of a backup that hasn't run yet.</summary>
    public const double DefaultMinutes = 60;
    /// <summary>Extra gap kept after each backup (a run can take longer than last time).</summary>
    public const double MarginMinutes = 15;
    /// <summary>How far ahead runs are compared (covers weekly and monthly schedules).</summary>
    public const int DaysAhead = 35;

    public sealed record Run(string Computer, string Job, bool IsThisPc, bool Full, Schedule Schedule, double? Minutes)
    {
        public double Length => Minutes is double m && m > 0 ? m : DefaultMinutes;
        public string KindText => Full ? "Full" : "Incremental";
        public string LengthText => Minutes is double m && m > 0
            ? m < 1.5 ? "about a minute" : m < 90 ? $"about {Math.Round(m):0} min" : $"about {m / 60:0.#} h"
            : "not run yet";
    }

    public sealed record Clash(bool MineFull, Run Other, DateTime FirstLocal);

    /// <summary>The backups other PCs (and this PC's other jobs) run to the place <paramref name="destination"/> is in.</summary>
    public static List<Run> Others(string destination, Guid thisMachine, string thisJob)
    {
        var runs = new List<Run>();
        string folder = WeeklyReport.StatusFolder(destination);
        if (!Directory.Exists(folder)) return runs;
        foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (Path.GetFileName(f).StartsWith("_")) continue;
            PcStatus? note;
            try { note = JsonSerializer.Deserialize<PcStatus>(File.ReadAllText(f), JobStore.Json); }
            catch { continue; }
            if (note == null) continue;
            bool me = note.MachineId == thisMachine;
            foreach (var j in note.Jobs)
            {
                if (me && string.Equals(j.Name, thisJob, StringComparison.OrdinalIgnoreCase)) continue; // the job being edited
                string pc = me ? "This PC" : note.ComputerName;
                if (j.Full is { Kind: not ScheduleKind.None } full) runs.Add(new Run(pc, j.Name, me, true, full, j.FullMinutes));
                if (j.Incremental is { Kind: not ScheduleKind.None } incr) runs.Add(new Run(pc, j.Name, me, false, incr, j.IncrementalMinutes));
            }
        }
        return runs.OrderBy(r => r.IsThisPc ? 0 : 1).ThenBy(r => r.Computer, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Job).ThenBy(r => r.Full ? 0 : 1).ToList();
    }

    /// <summary>Where this job's full and incremental runs would overlap another backup in the next few weeks (the first overlap of each pair).</summary>
    public static List<Clash> Find(Schedule? full, double? fullMinutes, Schedule? incr, double? incrMinutes, IEnumerable<Run> others, DateTime fromLocal)
    {
        var clashes = new List<Clash>();
        var upTo = fromLocal.AddDays(DaysAhead);
        var otherRuns = others.Select(o => (Run: o, Times: o.Schedule.Occurrences(fromLocal.AddDays(-1), upTo).ToList())).ToList();
        foreach (var (sched, minutes, isFull) in new[] { (full, fullMinutes, true), (incr, incrMinutes, false) })
        {
            if (sched == null || sched.Kind == ScheduleKind.None) continue;
            double mine = (minutes is double m && m > 0 ? m : DefaultMinutes) + MarginMinutes;
            var myTimes = sched.Occurrences(fromLocal, upTo).ToList();
            foreach (var (other, times) in otherRuns)
            {
                double theirs = other.Length + MarginMinutes;
                DateTime? first = null;
                foreach (var a in myTimes)
                {
                    if (times.Any(b => a < b.AddMinutes(theirs) && b < a.AddMinutes(mine))) { first = a; break; }
                }
                if (first is DateTime when) clashes.Add(new Clash(isFull, other, when)); // one per (this job's run, other run)
            }
        }
        return clashes.OrderBy(c => c.FirstLocal).ToList();
    }

    /// <summary>
    /// The nearest time to the schedule's own (trying every half hour, later first) at which it wouldn't overlap
    /// any of <paramref name="others"/>, or null if every time of day is taken.
    /// </summary>
    public static string? SuggestTime(Schedule sched, double? minutes, IEnumerable<Run> others, DateTime fromLocal)
    {
        if (sched.Kind == ScheduleKind.None || !TimeSpan.TryParse(sched.Time, out var start)) return null;
        var list = others.ToList();
        for (int step = 1; step < 48; step++)
        {
            foreach (int dir in new[] { 1, -1 })
            {
                var t = TimeSpan.FromMinutes(((start.TotalMinutes + dir * step * 30) % 1440 + 1440) % 1440);
                var candidate = new Schedule { Kind = sched.Kind, Days = sched.Days, DayOfMonth = sched.DayOfMonth, Time = $"{t.Hours:D2}:{t.Minutes:D2}" };
                if (Find(candidate, minutes, null, null, list, fromLocal).Count == 0) return candidate.Time;
            }
        }
        return null;
    }
}
