using Avalonia.Controls;
using Avalonia.Media;
using DiskImager.Core;

namespace DiskImager.Rescue.Pages;

/// <summary>Before / after bars, the erase warning, and the final go.</summary>
public sealed class ConfirmPage : UserControl
{
    // Hooks for the demo self-test.
    private readonly Action _confirm;
    internal string? Problem { get; }
    internal void Confirm() => _confirm();

    public ConfirmPage(MainWindow w)
    {
        var img = w.State.Image!;
        var h = img.Header;
        var target = w.State.Target!;
        LayoutPlan plan;
        string? problem;
        using (var reader = new ImageReader(img.Path))
        {
            plan = Layout.Original(SourceDescription.FromImage(reader), target.Size, target.SectorSize);
            Layout.AutoFit(plan);
            problem = plan.Validate() ?? (plan.NeedsShrink
                ? $"This disk ({Util.FormatBytes(target.Size)}) is smaller than the backup's partitions, and shrinking isn't available in the rescue system. Choose a disk of at least {Util.FormatBytes(plan.Parts.Max(p => p.Source.Offset + p.Source.Length))}, or restore from Windows."
                : null);
        }

        var before = Ui.Bar(h.DiskSize, plan.Parts.Select(p => (p.Source.Offset, p.Source.Length, $"{p.Source.Name}  {Util.FormatBytes(p.Source.Length)}", Ui.ColorFor(p.Source.Type, p.Source.FileSystem))));
        var after = Ui.Bar(target.Size, plan.Parts.Select(p => (p.TargetOffset, p.TargetLength, $"{p.Source.Name}  {Util.FormatBytes(p.TargetLength)}", Ui.ColorFor(p.Source.Type, p.Source.FileSystem))));
        var changes = plan.Parts.Where(p => p.TargetLength != p.Source.Length)
            .Select(p => $"{p.Source.Name}: {Util.FormatBytes(p.Source.Length)} → {Util.FormatBytes(p.TargetLength)}").ToList();

        var keepIds = new CheckBox
        {
            IsChecked = w.State.KeepIds,
            Content = "Keep the original disk IDs (recommended when this drive replaces the PC's own drive)",
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        bool armed = false;
        Button go = null!;
        go = Ui.Button("Erase and restore", () =>
        {
            if (!armed)
            {
                armed = true;
                go.Content = $"Yes, erase Disk {target.Number} now";
                return;
            }
            w.State.KeepIds = keepIds.IsChecked == true;
            w.Show(new RestorePage(w, plan), 4);
        }, "danger", enabled: problem == null);
        _confirm = () => { w.State.KeepIds = keepIds.IsChecked == true; w.Show(new RestorePage(w, plan), 4); };
        Problem = problem;

        Content = Ui.Stack(0,
            Ui.Title("Check and confirm"),
            Ui.Subtitle($"Restore {h.KindText.ToLowerInvariant()} of {h.ComputerName} from {h.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} onto Disk {target.Number} · {target.Model} ({Util.FormatBytes(target.Size)})."),
            Ui.Label($"Before · the backup ({Util.FormatBytes(h.DiskSize)} disk)"), before,
            new Border { Height = 14 },
            Ui.Label($"After · Disk {target.Number} ({Util.FormatBytes(target.Size)})"), after,
            changes.Count > 0 ? Ui.Banner(string.Join("\n", changes)) : new Border(),
            problem != null
                ? Ui.Banner(problem, danger: true)
                : Ui.Banner($"⚠  Everything on Disk {target.Number} ({target.Model}) will be erased. This can't be undone.", danger: true),
            keepIds,
            Ui.ButtonRow(Ui.Button("Back", () => w.Show(new ChooseDiskPage(w), 2)), go));
    }
}
