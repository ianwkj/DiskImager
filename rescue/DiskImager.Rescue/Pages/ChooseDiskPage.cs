using Avalonia.Controls;
using Avalonia.Media;
using DiskImager.Core;

namespace DiskImager.Rescue.Pages;

/// <summary>Which disk to restore onto (the rescue stick and the backup's own drive can't be picked).</summary>
public sealed class ChooseDiskPage : UserControl
{
    private readonly MainWindow _w;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly Button _next;
    private DiskInfo? _picked;

    public ChooseDiskPage(MainWindow w)
    {
        _w = w;
        var h = w.State.Image!.Header;
        long needs = h.Partitions.Count == 0 ? h.DiskSize : h.Partitions.Max(p => p.Offset + p.Length);
        _next = Ui.Button("Next", () => { w.State.Target = _picked; w.Show(new ConfirmPage(w), 3); }, "primary", enabled: false);
        Content = Ui.Stack(0,
            Ui.Title("Restore onto which disk?"),
            Ui.Subtitle($"{h.ComputerName} · {h.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} · the backup's disk was {Util.FormatBytes(h.DiskSize)} " +
                        $"(its partitions need {Util.FormatBytes(needs)} as they are; they're resized to fit a bigger or smaller disk). The disk you pick will be completely replaced."),
            _list,
            Ui.ButtonRow(Ui.Button("Back", () => w.Show(new FindBackupPage(w), 1)), _next));
        AttachedToVisualTree += async (_, _) => { await Load(); _loaded.TrySetResult(); };
    }

    // Hooks for the demo self-test.
    private readonly TaskCompletionSource _loaded = new();
    internal Task LoadTask => _loaded.Task;
    internal void PickLast()
    {
        if (_list.Children.OfType<Border>().LastOrDefault(b => b.Tag is DiskInfo d && !d.IsSystem)?.Tag is DiskInfo last) Pick(last);
    }
    internal void Next() { _w.State.Target = _picked; _w.Show(new ConfirmPage(_w), 3); }

    private async Task Load()
    {
        _list.Children.Add(Ui.Hint("Reading disks…"));
        List<DiskInfo> disks;
        int? imageDisk;
        try
        {
            disks = await Task.Run(_w.Platform.GetDisks);
            imageDisk = await Task.Run(() => _w.Platform.DiskOfPath(_w.State.Image!.Path));
        }
        catch (Exception ex) { _list.Children.Clear(); _list.Children.Add(Ui.Banner("Couldn't read the disks: " + ex.Message, danger: true)); return; }
        _list.Children.Clear();
        foreach (var d in disks)
        {
            string? why = d.IsSystem ? "rescue stick" : d.Number == imageDisk ? "holds the backup" : d.SectorSize != _w.State.Image!.Header.SectorSize ? "different sector size" : null;
            var head = Ui.Columns("Auto,*,Auto,Auto",
                Ui.Text(d.BusType == "USB" ? "🔌" : "💽", 20),
                Ui.Stack(2, Ui.Text($"Disk {d.Number} · {d.Model}", 14, FontWeight.SemiBold), Ui.Hint($"{d.BusType} · {d.Style}{(d.Serial.Length > 0 ? " · S/N " + d.Serial : "")}")),
                Ui.Text(Util.FormatBytes(d.Size), 15, FontWeight.SemiBold),
                why != null ? Ui.Pill(why, "#F1F5F9", "#475569") : new Border());
            head.Children[1].Margin = new Avalonia.Thickness(12, 0, 12, 0);
            if (why != null) head.Children[3].Margin = new Avalonia.Thickness(10, 0, 0, 0);
            var disk = d;
            var card = Ui.Card(Ui.Stack(10, head, Ui.DiskBar(d)), () => Pick(disk), disabled: why != null);
            card.Tag = d;
            _list.Children.Add(card);
        }
    }

    private void Pick(DiskInfo d)
    {
        _picked = d;
        foreach (var c in _list.Children.OfType<Border>())
        {
            c.Classes.Remove("selected");
            if (c.Tag == d) c.Classes.Add("selected");
        }
        _next.IsEnabled = true;
    }
}
