namespace DiskImager.Core;

/// <summary>Writes to %ProgramData%\DiskImager\Logs\yyyy-MM-dd.log and raises an event for the UI.</summary>
public sealed class Logger
{
    private static readonly object FileLock = new();

    public event Action<string>? LineWritten;

    public string FilePath => System.IO.Path.Combine(LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static string LogFolder => System.IO.Path.Combine(JobStore.DataFolder, "Logs");

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { }
        }
        LineWritten?.Invoke(line);
    }
}
