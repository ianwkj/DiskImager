using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskImager.Core;

namespace DiskImager;

/// <summary>"Scheduled backup starting in 10 seconds" pop-up shown by the tray helper.</summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DateTime _endsUtc;
    private readonly double _totalSeconds;

    public Guid PromptId { get; }
    public bool Answered { get; private set; }

    public CountdownWindow(BackgroundStatus s)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        PromptId = s.PromptId;
        _endsUtc = s.CountdownEndsUtc ?? DateTime.UtcNow + JobRunner.CountdownLength;
        _totalSeconds = Math.Max(1, JobRunner.CountdownLength.TotalSeconds);
        JobText.Text = s.JobName;
        DescText.Text = s.Description;
        DescText.ToolTip = s.Description;
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 4;
            Top = area.Bottom - ActualHeight - 4;
            Tick();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        double left = Math.Max(0, (_endsUtc - DateTime.UtcNow).TotalSeconds);
        Bar.Value = left / _totalSeconds;
        int secs = (int)Math.Ceiling(left);
        CountText.Text = secs > 0 ? $"Starting in {secs} second{(secs == 1 ? "" : "s")}…" : "Starting…";
        if (left <= 0) Close();
    }

    private void Answer(string action, int minutes = 0)
    {
        if (Answered) return;
        Answered = true;
        AgentChannel.Respond(PromptId, action, minutes);
        Close();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Answer(UserAction.Start);

    private void Postpone_Click(object sender, RoutedEventArgs e)
    {
        var menu = PostponeButton.ContextMenu!;
        menu.PlacementTarget = PostponeButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PostponeFor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string t } && int.TryParse(t, out int minutes))
            Answer(UserAction.Postpone, minutes);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Answer(UserAction.Cancel);

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) => Links.OnRequestNavigate(sender, e);
}
