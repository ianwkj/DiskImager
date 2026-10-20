using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiskImager.Core;

namespace DiskImager;

/// <summary>
/// "DiskImager x.y.z is available" pop-up. With a countdown it updates by itself unless the user clicks Later;
/// without one (Check now, or after a failed attempt) it waits for Update now.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly Logger _log;
    private readonly Func<string?> _blockedReason;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DateTime _endsUtc;
    private readonly double _totalSeconds;
    private readonly bool _countdown;
    private CancellationTokenSource? _cts;
    private bool _working;

    /// <summary>Set when the installer was started: the caller must close DiskImager.</summary>
    public bool Installing { get; private set; }

    /// <param name="blockedReason">Checked right before installing; a reason (e.g. "a backup is running") stops it.</param>
    public UpdateWindow(UpdateInfo update, TimeSpan? countdown, Func<string?> blockedReason, Logger log)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        _update = update;
        _log = log;
        _blockedReason = blockedReason;
        _countdown = countdown is { TotalSeconds: > 0 };
        _totalSeconds = Math.Max(1, countdown?.TotalSeconds ?? 1);
        _endsUtc = DateTime.UtcNow.AddSeconds(_totalSeconds);

        TitleText.Text = $"DiskImager {update.Version}";
        CurrentText.Text = $"You have version {Updater.Current}." + (update.Size > 0 ? $"  Download: {Util.FormatBytes(update.Size)}." : "");
        string notes = Updater.ShortNotes(update.Notes);
        NotesText.Text = notes;
        NotesBorder.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_countdown)
        {
            _timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => { Tick(); _timer.Start(); };
        }
        else
        {
            CountText.Text = "Install it now? DiskImager will close and reopen in about a minute.";
            Bar.Visibility = Visibility.Collapsed;
        }
        Closed += (_, _) => { _timer.Stop(); _cts?.Cancel(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private void Tick()
    {
        double left = Math.Max(0, (_endsUtc - DateTime.UtcNow).TotalSeconds);
        Bar.Value = left / _totalSeconds;
        int secs = (int)Math.Ceiling(left);
        CountText.Text = secs > 0 ? $"Updating automatically in {secs} second{(secs == 1 ? "" : "s")}…" : "Updating…";
        if (left <= 0)
        {
            _timer.Stop();
            _ = Install();
        }
    }

    private async Task Install()
    {
        if (_working) return;
        _working = true;
        _timer.Stop();

        string? blocked = _blockedReason();
        if (blocked != null)
        {
            _log.Info($"Update to {_update.Version} postponed because {blocked}");
            ShowMessage($"Not updating now because {blocked} You'll be asked again next time DiskImager opens.");
            return;
        }

        Buttons.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        CountText.Text = "Downloading the update…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                Bar.Value = p;
                CountText.Text = $"Downloading the update… {p:P0}";
            });
            string setup = await Updater.DownloadAsync(_update, progress, _log, _cts.Token);

            blocked = _blockedReason(); // a backup may have started during the download
            if (blocked != null) throw new InvalidOperationException(blocked);

            CountText.Text = "Installing… DiskImager will close and reopen in about a minute.";
            Bar.IsIndeterminate = true;
            Updater.StartInstall(setup, _log);
            Installing = true;
            await Task.Delay(1500);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"Update to {_update.Version} failed: {ex.Message}");
            ShowMessage("The update couldn't be installed: " + ex.Message);
        }
    }

    private void ShowMessage(string text)
    {
        CountText.Text = text;
        Bar.IsIndeterminate = false;
        Bar.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Visible;
        UpdateButton.Visibility = Visibility.Collapsed;
        LaterButton.Content = "Close";
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _ = Install();

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (!_working && UpdateButton.Visibility == Visibility.Visible)
            _log.Info($"Update to {_update.Version} postponed until DiskImager next opens.");
        Close();
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) => Links.OnRequestNavigate(sender, e);
}
