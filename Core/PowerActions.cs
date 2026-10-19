using System.Diagnostics;

namespace DiskImager.Core;

public static class PowerActions
{
    public static void Execute(PostAction action, Logger log)
    {
        if (action == PostAction.None) return;
        log.Info($"After-backup action: {action}.");
        const string msg = "DiskImager has finished. The computer will shut down in 60 seconds. Run 'shutdown /a' to cancel.";
        switch (action)
        {
            case PostAction.Shutdown:
                Run("/s /f /t 60 /c \"" + msg + "\"");
                break;
            case PostAction.Restart:
                Run("/r /f /t 60 /c \"" + msg.Replace("shut down", "restart") + "\"");
                break;
            case PostAction.Hibernate:
                Run("/h");
                break;
            case PostAction.Sleep:
                Native.SetSuspendState(false, false, false);
                break;
        }
    }

    private static void Run(string args)
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
    }
}
