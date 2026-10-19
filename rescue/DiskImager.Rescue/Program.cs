using Avalonia;
using DiskImager.Core;
using DiskImager.Rescue.Platform;

namespace DiskImager.Rescue;

/// <summary>
/// diskimager-rescue            the rescue app on the Linux stick (run as root)
/// diskimager-rescue --demo DIR  the same app with demo disks in DIR (development and tests, also on Windows)
/// </summary>
public static class Program
{
    public static IRescuePlatform Platform { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        int demo = Array.IndexOf(args, "--demo");
        if (demo >= 0 && demo + 1 < args.Length)
        {
            Platform = new DemoPlatform(args[demo + 1]);
            Logger.FolderOverride = Path.Combine(args[demo + 1], "logs");
        }
        else if (OperatingSystem.IsLinux())
        {
            Platform = new LinuxPlatform();
            Logger.FolderOverride = "/var/log/diskimager";
        }
        else
        {
            Platform = new DemoPlatform(Path.Combine(Path.GetTempPath(), "diskimager-rescue-demo"));
            Logger.FolderOverride = Path.Combine(Path.GetTempPath(), "diskimager-rescue-demo", "logs");
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
