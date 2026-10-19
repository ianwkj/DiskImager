using Avalonia.Controls;
using Avalonia.Media;
using DiskImager.Core;
using DiskImager.Rescue.Platform;

namespace DiskImager.Rescue.Pages;

/// <summary>What each drive reports about its own health (SMART).</summary>
public sealed class HealthPage : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 10 };

    public HealthPage(MainWindow w)
    {
        Content = Ui.Stack(0,
            Ui.Title("Disk health"),
            Ui.Subtitle("What each drive reports about itself. A drive that reports problems should be backed up and replaced soon."),
            _list,
            Ui.ButtonRow(Ui.Button("Back", () => w.GoHome(), "primary")));
        AttachedToVisualTree += async (_, _) =>
        {
            _list.Children.Add(Ui.Hint("Asking the drives…"));
            List<DiskHealth> health;
            try { health = await Task.Run(() => w.Platform.CheckHealth(w.Log)); }
            catch (Exception ex) { _list.Children.Clear(); _list.Children.Add(Ui.Banner("Couldn't check the drives: " + ex.Message, danger: true)); return; }
            _list.Children.Clear();
            foreach (var h in health)
            {
                var pill = h.Status switch
                {
                    HealthStatus.Good => Ui.Pill("Healthy", "#DCFCE7", "#166534"),
                    HealthStatus.Warning => Ui.Pill("Wearing out", "#FEF3C7", "#92400E"),
                    HealthStatus.Failing => Ui.Pill("Failing", "#FEE2E2", "#991B1B"),
                    _ => Ui.Pill("No report", "#F1F5F9", "#475569"),
                };
                var details = Ui.Row(18, h.Details.Select(d => (Control)Ui.Stack(1, Ui.Hint(d.Label), Ui.Text(d.Value, 14, FontWeight.SemiBold))).ToArray());
                _list.Children.Add(Ui.Card(Ui.Stack(8,
                    Ui.Columns("*,Auto", Ui.Stack(2, Ui.Text($"Disk {h.Disk.Number} · {h.Disk.Model} ({Util.FormatBytes(h.Disk.Size)})", 15, FontWeight.SemiBold), Ui.Hint(h.Summary)), pill),
                    details)));
            }
        };
    }
}
