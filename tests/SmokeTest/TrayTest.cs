// Live test of the tray helper: run with  dotnet run -- --tray <path to DiskImager.exe> <screenshot folder>
// Plays the part of a scheduled backup (status file + countdown), clicks "Start now" via UI Automation,
// and saves screenshots of the bottom-right of the screen at each stage.
using System.Diagnostics;
using System.Windows.Automation;
using DiskImager.Core;

static class TrayTest
{
    public static int Run(string exe, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        Directory.CreateDirectory(shots);

        var agent = Process.Start(new ProcessStartInfo(exe, "--agent") { UseShellExecute = false });
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!AgentChannel.AnyAgentAlive() && DateTime.UtcNow < until) Thread.Sleep(250);
        Check(AgentChannel.AnyAgentAlive(), "tray helper starts and reports it's alive");

        using (var status = new BackgroundStatus.Publisher("Nightly C drive", "Disk 1 – NVMe SSD (1 TB)  →  \\\\NAS\\Backups\\OFFICE-PC"))
        {
            // 1. Countdown pop-up, answered by clicking "Start now".
            var prompt = Guid.NewGuid();
            status.SetCountdown(prompt, DateTime.UtcNow + JobRunner.CountdownLength);
            Thread.Sleep(3000);
            Shot(shots, "tray-1-countdown");
            var popup = FindWindow("DiskImager – scheduled backup", TimeSpan.FromSeconds(3));
            Check(popup != null, "countdown pop-up appears");
            var start = popup?.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Start now")));
            Check(start != null, "pop-up has a Start now button");
            ((InvokePattern?)start?.GetCurrentPattern(InvokePattern.Pattern))?.Invoke();
            var answer = AgentChannel.WaitForResponse(prompt, TimeSpan.FromSeconds(5));
            Check(answer?.Action == UserAction.Start, $"clicking Start now reaches the backup (got {answer?.Action ?? "nothing"})");
            Thread.Sleep(1500);
            Check(FindWindow("DiskImager – scheduled backup", TimeSpan.FromSeconds(1)) == null, "pop-up closes after answering");

            // 2. A second countdown left alone: closes by itself when time is up.
            var prompt2 = Guid.NewGuid();
            status.SetCountdown(prompt2, DateTime.UtcNow + JobRunner.CountdownLength);
            var noAnswer = AgentChannel.WaitForResponse(prompt2, JobRunner.CountdownLength + TimeSpan.FromSeconds(3));
            Check(noAnswer == null, "no answer within 10 s means the backup just starts");

            // 3. Running: tray icon, taskbar button with progress.
            status.SetRunning();
            status.Report(new ProgressInfo("Creating full image", 42L << 30, 100L << 30, 180 << 20));
            Thread.Sleep(3000);
            Shot(shots, "tray-2-running");
            var prog = FindWindow("DiskImager – backing up 'Nightly C drive'", TimeSpan.FromSeconds(3));
            Check(prog != null, "progress window (taskbar button) appears while running");
            if (prog != null)
            {
                ((WindowPattern)prog.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
                Thread.Sleep(1200);
                ShotWindow(shots, "tray-3-progress-window", prog);
            }

            // 4. Stop from the progress window -> cancel request reaches the backup.
            AgentChannel.RequestCancel(Environment.ProcessId);
            Check(AgentChannel.CancelRequested(Environment.ProcessId) != null, "stop request reaches the backup");

            // 5. Finished: notification.
            status.Finish("Backup completed: 98 GB in 42m 00s.", true);
            Thread.Sleep(2500);
            Shot(shots, "tray-4-finished");
        }

        AgentChannel.AskAgentsToExit();
        Check(agent.WaitForExit(8000), "tray helper exits when asked (so the installed copy can be updated)");
        Console.WriteLine(failures == 0 ? "\nTRAY TESTS PASSED" : $"\n{failures} TRAY TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static AutomationElement? FindWindow(string title, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            var w = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, title));
            if (w != null) return w;
            Thread.Sleep(200);
        } while (DateTime.UtcNow < until);
        return null;
    }

    private static void Shot(string folder, string name)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        int w = Math.Min(900, screen.Width), h = Math.Min(560, screen.Height);
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(screen.Right - w, screen.Bottom - h, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void ShotWindow(string folder, string name, AutomationElement window)
    {
        var r = window.Current.BoundingRectangle;
        using var bmp = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }
}
