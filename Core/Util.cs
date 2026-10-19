namespace DiskImager.Core;

public static class Util
{
    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";

    public static string SafeName(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? "Backup" : s.Trim();
    }
}

public sealed record ProgressInfo(string Stage, long Done, long Total, double BytesPerSecond);

/// <summary>Turns raw byte counts into throttled progress reports with a speed figure.</summary>
public sealed class ProgressTracker
{
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly string _stage;
    private readonly long _total;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private long _lastReport;

    public ProgressTracker(IProgress<ProgressInfo>? progress, string stage, long total)
    {
        _progress = progress;
        _stage = stage;
        _total = total;
        _progress?.Report(new ProgressInfo(stage, 0, total, 0));
    }

    public void Report(long done, bool force = false)
    {
        long now = _sw.ElapsedMilliseconds;
        if (!force && now - _lastReport < 250) return;
        _lastReport = now;
        double speed = now > 0 ? done / (now / 1000.0) : 0;
        _progress?.Report(new ProgressInfo(_stage, done, _total, speed));
    }
}
