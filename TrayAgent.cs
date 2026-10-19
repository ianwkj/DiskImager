using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DiskImager.Core;
using Forms = System.Windows.Forms;

namespace DiskImager;

/// <summary>
/// Runs in each signed-in user's session (DiskImager.exe --agent, started at sign-in). Scheduled backups
/// run as SYSTEM and can't show anything on screen, so this helper does it for them:
///  - the 10-second "starting soon" pop-up (Start now / Postpone / Cancel),
///  - a tray icon and a taskbar button with progress while a backup runs,
///  - a notification when it finishes.
/// </summary>
public sealed class TrayAgent
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<Guid> _promptsShown = new();
    private readonly HashSet<int> _announced = new();
    private Forms.NotifyIcon _icon = null!;
    private Forms.ToolStripMenuItem _stopItem = null!;
    private CountdownWindow? _countdown;
    private ProgressWindow? _progress;
    private DateTime _lastHeartbeat;
    private DateTime? _hideIconAt;
    private int _activePid;

    public void Start()
    {
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Text = "DiskImager", Visible = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show progress", null, (_, _) => ShowProgress());
        _stopItem = new Forms.ToolStripMenuItem("Stop backup…", null, (_, _) => StopBackup());
        menu.Items.Add(_stopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open DiskImager", null, (_, _) => OpenMainApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowProgress();

        AgentChannel.Heartbeat();
        _lastHeartbeat = DateTime.UtcNow;
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        return res != null ? new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    private void Poll()
    {
        if (AgentChannel.ExitRequestedSince(_startedUtc))
        {
            Exit();
            return;
        }
        if (DateTime.UtcNow - _lastHeartbeat > TimeSpan.FromSeconds(5))
        {
            AgentChannel.Heartbeat();
            _lastHeartbeat = DateTime.UtcNow;
        }

        var runs = BackgroundStatus.ReadAll(includeFinished: true);

        // 1. The "starting in 10 seconds" pop-up.
        var prompt = runs.FirstOrDefault(r => r.Phase == RunPhase.Countdown && !_promptsShown.Contains(r.PromptId)
                                              && r.CountdownEndsUtc > DateTime.UtcNow);
        if (prompt != null && _countdown == null)
        {
            _promptsShown.Add(prompt.PromptId);
            _countdown = new CountdownWindow(prompt);
            _countdown.Closed += (_, _) => _countdown = null;
            _countdown.Show();
            _countdown.Activate();
        }
        if (_countdown != null && !runs.Any(r => r.Phase == RunPhase.Countdown && r.PromptId == _countdown.PromptId)
            && DateTime.UtcNow - _startedUtc > TimeSpan.FromSeconds(2))
            _countdown.Close(); // answered elsewhere, or the backup moved on

        // 2. Announce results.
        foreach (var f in runs.Where(r => r.Phase == RunPhase.Finished && !_announced.Contains(r.ProcessId)))
        {
            _announced.Add(f.ProcessId);
            if (f.UpdatedUtc < _startedUtc) continue; // finished before this helper started
            _icon.Visible = true;
            _icon.ShowBalloonTip(8000, f.Success ? $"Backup '{f.JobName}' completed" : $"Backup '{f.JobName}' didn't complete",
                f.Result ?? "", f.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
            _hideIconAt = DateTime.UtcNow.AddSeconds(15);
        }

        // 3. Tray icon + taskbar progress while something is queued or running.
        var active = runs.FirstOrDefault(r => r.Phase == RunPhase.Running)
                     ?? runs.FirstOrDefault(r => r.Phase is RunPhase.Waiting or RunPhase.Postponed or RunPhase.Countdown);
        if (active != null)
        {
            _activePid = active.ProcessId;
            _hideIconAt = null;
            _icon.Visible = true;
            _icon.Text = Trim($"DiskImager – {active.JobName}: {StatusLine(active)}", 127);
            _stopItem.Enabled = active.Phase == RunPhase.Running;

            if (active.Phase == RunPhase.Running && _progress == null)
            {
                _progress = new ProgressWindow { ShowActivated = false };
                _progress.Update(active);
                _progress.Show();
                _progress.WindowState = WindowState.Minimized; // taskbar button only; click to open
                _icon.ShowBalloonTip(5000, $"Backup '{active.JobName}' started",
                    "It runs in the background. Double-click the DiskImager icon to see progress.", Forms.ToolTipIcon.Info);
            }
            _progress?.Update(active);
        }
        else
        {
            if (_progress != null)
            {
                _progress.AllowClose = true;
                _progress.Close();
                _progress = null;
            }
            _hideIconAt ??= DateTime.UtcNow.AddSeconds(15);
            if (DateTime.UtcNow > _hideIconAt) _icon.Visible = false;
        }
    }

    private static string StatusLine(BackgroundStatus s) => s.Phase switch
    {
        RunPhase.Running when s.Total > 0 => $"{100.0 * s.Done / s.Total:0}%",
        RunPhase.Running => "starting",
        RunPhase.Waiting => "waiting for another backup",
        RunPhase.Postponed => $"postponed until {s.PostponedUntilUtc?.ToLocalTime():HH:mm}",
        RunPhase.Countdown => "starting soon",
        _ => s.Stage,
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void ShowProgress()
    {
        if (_progress == null) return;
        _progress.WindowState = WindowState.Normal;
        _progress.Activate();
    }

    private void StopBackup()
    {
        if (_activePid == 0) return;
        if (MessageBox.Show("Stop the backup that's running now? Nothing from this run will be saved; earlier backups are kept.",
                "DiskImager", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            AgentChannel.RequestCancel(_activePid);
    }

    private static void OpenMainApp()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); }
        catch { }
    }

    private void Exit()
    {
        _poll.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        AgentChannel.RemoveHeartbeat();
        Application.Current.Shutdown();
    }
}
