using Avalonia.Controls;
using Avalonia.Media;
using DiskImager.Core;

namespace DiskImager.Rescue.Pages;

/// <summary>The restore itself: progress, speed and time left, then "remove the USB and restart".</summary>
public sealed class RestorePage : UserControl
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 10, CornerRadius = new Avalonia.CornerRadius(999) };
    private readonly TextBlock _stage = Ui.Text("Starting…", 14);
    private readonly TextBlock _pct = Ui.Text("", 14, FontWeight.SemiBold);
    private readonly TextBlock _amount = Ui.Hint("");
    private readonly TextBlock _speed = Ui.Hint("");
    private readonly StackPanel _result = new();

    public RestorePage(MainWindow w, LayoutPlan plan)
    {
        var s = w.State;
        var h = s.Image!.Header;
        Content = Ui.Stack(0,
            Ui.Title("Restoring…"),
            Ui.Subtitle($"{h.ComputerName} · {h.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} → Disk {s.Target!.Number} · {s.Target.Model}. You can leave the PC; it shows here when it's done."),
            Ui.Card(Ui.Stack(8,
                Ui.Columns("*,Auto", _stage, _pct),
                _bar,
                Ui.Columns("*,Auto", _amount, _speed))),
            _result);
        AttachedToVisualTree += async (_, _) => { await Run(w, plan); _finished.TrySetResult(); };
    }

    // Hooks for the demo self-test.
    private readonly TaskCompletionSource _finished = new();
    internal Task Finished => _finished.Task;
    internal bool Succeeded { get; private set; }

    private async Task Run(MainWindow w, LayoutPlan plan)
    {
        var s = w.State;
        var started = DateTime.UtcNow;
        var progress = new Progress<ProgressInfo>(p =>
        {
            _stage.Text = p.Stage;
            if (p.Total > 0)
            {
                double f = (double)p.Done / p.Total;
                _bar.IsIndeterminate = false;
                _bar.Value = f;
                _pct.Text = $"{f:P0}";
                _amount.Text = $"{Util.FormatBytes(p.Done)} of {Util.FormatBytes(p.Total)}";
                if (p.BytesPerSecond > 0)
                    _speed.Text = $"{Util.FormatBytes(p.BytesPerSecond)}/s · about {Util.FormatDuration(TimeSpan.FromSeconds((p.Total - p.Done) / p.BytesPerSecond))} left";
            }
            else { _bar.IsIndeterminate = true; _pct.Text = ""; _speed.Text = ""; }
        });

        w.Busy = true;
        try
        {
            await Task.Run(() =>
            {
                using var reader = new ImageReader(s.Image!.Path);
                var missing = reader.MissingChainFiles();
                if (missing.Count > 0) throw new InvalidOperationException(missing[0]);
                if (reader.IsEncrypted) reader.Unlock(s.Password);
                w.Platform.Restore(reader, s.Target!, plan, newIds: !s.KeepIds, progress, CancellationToken.None, w.Log);
            });
            Succeeded = true;
            _bar.IsIndeterminate = false;
            _bar.Value = 1;
            _pct.Text = "100%";
            _stage.Text = "Done";
            var grown = plan.Parts.Where(p => p.Grows).Select(p => $"{p.Source.Name} was enlarged to {Util.FormatBytes(p.TargetLength)}.");
            _result.Children.Add(Ui.Banner($"✅  Restore complete in {Util.FormatDuration(DateTime.UtcNow - started)}. {string.Join(" ", grown)} Remove the rescue USB, then restart.", success: true));
            _result.Children.Add(Ui.ButtonRow(Ui.Button("Back to start", () => w.GoHome()), Ui.Button("Restart now", () => w.Platform.Reboot(), "primary")));
        }
        catch (Exception ex)
        {
            w.Log.Error("Restore failed: " + ex);
            _stage.Text = "Stopped";
            _bar.IsIndeterminate = false;
            _result.Children.Add(Ui.Banner("The restore didn't finish: " + ex.Message + "\n\nThe disk may be incomplete. Fix the problem and restore again; the backup itself is unchanged.", danger: true));
            _result.Children.Add(Ui.ButtonRow(Ui.Button("Back to start", () => w.GoHome(), "primary")));
        }
        finally { w.Busy = false; }
    }
}
