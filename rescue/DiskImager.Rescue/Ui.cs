using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiskImager.Core;

namespace DiskImager.Rescue;

/// <summary>Small building blocks in DiskImager's look, shared by the pages.</summary>
public static class Ui
{
    public enum StepState { Done, Current, Todo }

    public static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock Title(string text) => new() { Text = text, Classes = { "h1" } };
    public static TextBlock Subtitle(string text) => new() { Text = text, Classes = { "sub" } };
    public static TextBlock Hint(string text) => new() { Text = text, Classes = { "hint" } };
    public static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "label" } };

    public static TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, string? color = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (color != null) t.Foreground = Hex(color); // otherwise inherit the page's text colour
        return t;
    }

    public static Border StepChip(string text, StepState s) => new()
    {
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(12, 4),
        Background = Hex(s switch { StepState.Done => "#DCFCE7", StepState.Current => "#DBEAFE", _ => "#F1F5F9" }),
        Child = new TextBlock
        {
            Text = (s == StepState.Done ? "✓ " : "") + text, FontSize = 12.5,
            FontWeight = s == StepState.Current ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Hex(s switch { StepState.Done => "#166534", StepState.Current => "#1D4ED8", _ => "#64748B" }),
        },
    };

    public static Border Pill(string text, string background, string foreground) => new()
    {
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(9, 2),
        Background = Hex(background),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Hex(foreground) },
    };

    /// <summary>A rounded square with an emoji, the page's "icon" style.</summary>
    public static Border IconTile(string emoji, string background, double size = 42) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(10), Background = Hex(background),
        VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = emoji, FontSize = size * 0.48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    public static Border Card(Control content, Action? onClick = null, bool selected = false, bool disabled = false)
    {
        var b = new Border { Child = content, Classes = { "card" } };
        if (selected) b.Classes.Add("selected");
        if (disabled) b.Classes.Add("disabled");
        else if (onClick != null)
        {
            b.Classes.Add("clickable");
            b.Tapped += (_, _) => onClick();
        }
        return b;
    }

    public static Button Button(string text, Action onClick, string? cls = null, bool enabled = true)
    {
        var b = new Button { Content = text, IsEnabled = enabled };
        if (cls != null) b.Classes.Add(cls);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Back / Next style row aligned right.</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static Border Banner(string text, bool danger = false, bool success = false) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 10),
        Margin = new Thickness(0, 12, 0, 0),
        Background = Hex(danger ? "#FEF2F2" : success ? "#F0FDF4" : "#F8FAFC"),
        BorderBrush = Hex(danger ? "#FECACA" : success ? "#BBF7D0" : "#E2E8F0"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Hex(danger ? "#991B1B" : success ? "#166534" : "#334155") },
    };

    public static string ColorFor(string type, string fs) => type switch
    {
        _ when type.Contains("EFI", StringComparison.OrdinalIgnoreCase) => "#14B8A6",
        _ when type.Contains("Microsoft reserved", StringComparison.OrdinalIgnoreCase) || type == "MSR" => "#94A3B8",
        _ when type.Contains("Recovery", StringComparison.OrdinalIgnoreCase) => "#F59E0B",
        _ when fs == "NTFS" => "#2563EB",
        _ when fs is "FAT32" or "exFAT" => "#0EA5E9",
        _ => "#6366F1",
    };

    /// <summary>A partition bar: segments sized by length (small ones get a minimum width), unallocated space in grey.</summary>
    public static Control Bar(long diskSize, IEnumerable<(long Offset, long Length, string Name, string Color)> parts, double height = 26)
    {
        var grid = new Grid { Height = height, ClipToBounds = true };
        long pos = 0;
        int col = 0;
        void Add(long len, string name, string color, string fg = "#FFFFFF")
        {
            if (len <= 0) return;
            double weight = Math.Max(len, diskSize / 40.0);
            grid.ColumnDefinitions.Add(new ColumnDefinition(weight, GridUnitType.Star));
            var seg = new Border
            {
                Background = Hex(color), Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = name, FontSize = 11.5, Foreground = Hex(fg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0), TextTrimming = TextTrimming.CharacterEllipsis },
            };
            Grid.SetColumn(seg, col++);
            grid.Children.Add(seg);
        }
        foreach (var p in parts.OrderBy(p => p.Offset))
        {
            if (p.Offset - pos > diskSize / 100) Add(p.Offset - pos, "", "#E2E8F0");
            Add(p.Length, p.Name, p.Color);
            pos = Math.Max(pos, p.Offset + p.Length);
        }
        if (diskSize - pos > diskSize / 100) Add(diskSize - pos, pos == 0 ? "Empty" : "Unallocated", "#E2E8F0", "#475569");
        if (col == 0) Add(diskSize, "Empty", "#E2E8F0", "#475569");
        return grid;
    }

    public static Control DiskBar(DiskInfo d) => Bar(d.Size, d.Partitions.Where(p => !p.IsContainer).Select(p =>
        (p.Offset, p.Length,
         string.Join("  ", new[] { p.Volume?.Label, p.Volume?.FileSystem is { } fs && fs != "RAW" ? fs : null, Util.FormatBytes(p.Length) }.Where(s => !string.IsNullOrEmpty(s))),
         ColorFor(p.TypeName, p.Volume?.FileSystem ?? ""))));

    public static Grid Columns(string definition, params Control[] children)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(definition) };
        for (int i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            g.Children.Add(children[i]);
        }
        return g;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }
}
