using System.Windows;
using System.Windows.Shell;
using DiskImager.Core;

namespace DiskImager;

/// <summary>
/// Progress of a scheduled backup, shown by the tray helper. Its taskbar button shows the DiskImager
/// icon with a green progress fill, so people can see a backup is running.
/// </summary>
public partial class ProgressWindow : Window
{
    private int _processId;

    public bool AllowClose { get; set; }

    public ProgressWindow()
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        // Closing just hides to the taskbar; the tray helper decides when it really goes away.
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        };
    }

    public void Update(BackgroundStatus s)
    {
        _processId = s.ProcessId;
        JobText.Text = s.JobName;
        DescText.Text = s.Description;
        DescText.ToolTip = s.Description;
        Title = $"DiskImager – backing up '{s.JobName}'";

        switch (s.Phase)
        {
            case RunPhase.Waiting:
            case RunPhase.Postponed:
                StageText.Text = s.Stage;
                Bar.IsIndeterminate = false;
                Bar.Value = 0;
                DetailText.Text = "";
                Taskbar.ProgressState = TaskbarItemProgressState.Paused;
                Taskbar.ProgressValue = 1;
                break;
            default:
                StageText.Text = s.Stage;
                if (s.Total > 0)
                {
                    double frac = Math.Min(1, (double)s.Done / s.Total);
                    Bar.IsIndeterminate = false;
                    Bar.Value = frac * 100;
                    Taskbar.ProgressState = TaskbarItemProgressState.Normal;
                    Taskbar.ProgressValue = frac;
                    string eta = s.BytesPerSecond > 0 && s.Total > s.Done
                        ? $"  ·  about {Util.FormatDuration(TimeSpan.FromSeconds((s.Total - s.Done) / s.BytesPerSecond))} left" : "";
                    DetailText.Text = $"{frac * 100:0}%  ·  {Util.FormatBytes(s.Done)} of {Util.FormatBytes(s.Total)}  ·  {Util.FormatBytes(s.BytesPerSecond)}/s{eta}";
                }
                else
                {
                    Bar.IsIndeterminate = true;
                    Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
                    DetailText.Text = "Preparing…";
                }
                break;
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop this backup now? Nothing from this run will be saved; earlier backups are kept.",
                "Stop backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AgentChannel.RequestCancel(_processId);
        StageText.Text = "Stopping…";
        CancelButton.IsEnabled = false;
    }
}
