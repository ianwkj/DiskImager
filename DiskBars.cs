using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskImager.Core;

namespace DiskImager;

/// <summary>Draws disks as cards with a proportional partition bar (Disks tab, Restore tab, layout window).</summary>
public static class DiskBars
{
    public sealed record Segment(long Offset, long Length, string Label, string Sub, string Color, bool Dark = true);

    public static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFrom(hex)!;

    public static string ColorFor(string type, bool hasLetter) => type switch
    {
        "EFI System" => "#0EA5A4",
        "Recovery" => "#F59E0B",
        "Microsoft Reserved" => "#94A3B8",
        _ => hasLetter ? "#2563EB" : "#7C3AED",
    };

    /// <summary>Partition bar with unallocated gaps shown in grey; tiny partitions get a minimum width.</summary>
    public static Grid Bar(long diskSize, IEnumerable<Segment> partitions, double height = 50)
    {
        var bar = new Grid { Height = height };
        void Add(Segment s)
        {
            double frac = Math.Max((double)s.Length / Math.Max(1, diskSize), 0.07);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            var fg = s.Dark ? Brushes.White : Hex("#334155");
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = s.Label, Foreground = fg, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = s.Sub, Foreground = fg, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var seg = new Border
            {
                Background = Hex(s.Color), Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 4, 6, 4), ToolTip = $"{s.Label}\n{s.Sub}", Child = sp,
            };
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(seg);
        }

        long pos = 0;
        foreach (var s in partitions.OrderBy(s => s.Offset))
        {
            if (s.Offset - pos > diskSize / 50) Add(new Segment(pos, s.Offset - pos, "Unallocated", Util.FormatBytes(s.Offset - pos), "#E2E8F0", false));
            Add(s);
            pos = Math.Max(pos, s.Offset + s.Length);
        }
        if (diskSize - pos > diskSize / 50) Add(new Segment(pos, diskSize - pos, "Unallocated", Util.FormatBytes(diskSize - pos), "#E2E8F0", false));
        if (bar.Children.Count == 0) Add(new Segment(0, diskSize, "Empty disk", Util.FormatBytes(diskSize), "#E2E8F0", false));
        return bar;
    }

    public static IEnumerable<Segment> SegmentsOf(DiskInfo d) => d.DataPartitions.Select(p =>
        new Segment(p.Offset, p.Length, p.Describe(), Util.FormatBytes(p.Length), ColorFor(p.TypeName, p.Volume?.DriveLetter != null)));

    public static FrameworkElement Card(DiskInfo d, string? note = null, bool compact = false)
    {
        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = Hex("#E2E8F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = compact ? new Thickness(14, 10, 14, 12) : new Thickness(18, 14, 18, 16),
            Tag = d,
        };
        var stack = new StackPanel();
        card.Child = stack;

        // Header: icon tile, name + details, size on the right.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, compact ? 8 : 12) };
        bool usb = d.BusType is "USB" or "SD" or "MMC";
        var tile = new Border
        {
            Width = compact ? 32 : 40, Height = compact ? 32 : 40, CornerRadius = new CornerRadius(compact ? 8 : 10),
            Background = Hex(d.IsSystem ? "#DBEAFE" : usb ? "#FEF3C7" : "#F1F5F9"),
            Child = new TextBlock
            {
                Text = usb ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 19,
                Foreground = Hex(d.IsSystem ? "#1D4ED8" : usb ? "#B45309" : "#475569"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        header.Children.Add(tile);

        var size = new TextBlock
        {
            Text = Util.FormatBytes(d.Size), FontSize = compact ? 15 : 18, FontWeight = FontWeights.SemiBold, Foreground = Hex("#0F172A"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(size);

        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = $"Disk {d.Number}", FontWeight = FontWeights.SemiBold, FontSize = compact ? 14 : 15, Foreground = Hex("#0F172A") });
        title.Children.Add(new TextBlock { Text = "  ·  " + d.Model, FontSize = compact ? 14 : 15, Foreground = Hex("#334155"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (d.IsSystem) title.Children.Add(Badge("WINDOWS", "#DBEAFE", "#1D4ED8"));
        if (note != null) title.Children.Add(Badge(note, "#FEF3C7", "#92400E"));
        names.Children.Add(title);
        names.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", new[] { d.BusType, d.Style.ToString(), $"{d.SectorSize}-byte sectors", d.Serial.Length > 0 ? "S/N " + d.Serial : "" }
                .Where(s => s.Length > 0)),
            Foreground = Hex("#64748B"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(names);
        stack.Children.Add(header);
        stack.Children.Add(Bar(d.Size, SegmentsOf(d), compact ? 42 : 46));
        return card;
    }

    private static Border Badge(string text, string bg, string fg) => new()
    {
        Background = Hex(bg), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Hex(fg) },
    };
}
