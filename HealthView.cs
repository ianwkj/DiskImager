using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskImager.Core;

namespace DiskImager;

/// <summary>Draws the Disk health page: a card per drive on the left, the selected drive's details on the right.</summary>
public static class HealthView
{
    private static Brush Hex(string hex) => DiskBars.Hex(hex);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>(soft background, strong colour) for a status, like the app's badges.</summary>
    public static (string Soft, string Strong) Colors(HealthStatus s) => s switch
    {
        HealthStatus.Good => ("#DCFCE7", "#15803D"),
        HealthStatus.Caution => ("#FEF3C7", "#B45309"),
        HealthStatus.Bad => ("#FEE2E2", "#B91C1C"),
        _ => ("#F1F5F9", "#475569"),
    };

    /// <summary>Green while comfortably cool, amber within 5 °C of the alert limit, red above it.</summary>
    public static (string Soft, string Strong) TempColors(int? c, int limit) =>
        c == null ? Colors(HealthStatus.Unknown)
        : c > limit ? Colors(HealthStatus.Bad)
        : c > limit - 5 ? Colors(HealthStatus.Caution)
        : ("#E0F2FE", "#0369A1");

    // ------------------------------------------------------------------ left: one card per drive

    public static FrameworkElement Card(DriveHealth d, HealthSettings s)
    {
        var (soft, strong) = Colors(d.Status);
        var card = new Border
        {
            Background = Brushes.White, BorderBrush = Hex("#E2E8F0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Tag = d,
        };
        var dock = new DockPanel();
        card.Child = dock;

        var dot = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Hex(soft), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = d.Status switch { HealthStatus.Good => "", HealthStatus.Caution => "", HealthStatus.Bad => "", _ => "" },
                FontFamily = Icons, FontSize = 16, Foreground = Hex(strong),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = $"Disk {d.DiskNumber}  ·  {d.Model}", FontWeight = FontWeights.SemiBold, FontSize = 13.5,
            Foreground = Hex("#0F172A"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.Model,
        });
        var line = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        line.Children.Add(Pill(d.StatusText, soft, strong));
        if (d.TemperatureC is int t)
        {
            var (ts, tf) = TempColors(t, d.Kind == DriveKind.Hdd ? s.HotHddC : s.HotSsdC);
            line.Children.Add(Pill($"{t} °C", ts, tf));
        }
        line.Children.Add(new TextBlock
        {
            Text = $"{d.KindText} · {Util.FormatBytes(d.Size)}", FontSize = 12, Foreground = Hex("#64748B"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
        });
        text.Children.Add(line);
        dock.Children.Add(text);
        return card;
    }

    private static Border Pill(string text, string bg, string fg) => new()
    {
        Background = Hex(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Hex(fg) },
    };

    // ------------------------------------------------------------------ right: details of the selected drive

    public static FrameworkElement Details(DriveHealth d, HealthSettings s)
    {
        var page = new StackPanel();
        page.Children.Add(Summary(d, s));

        if (d.Unavailable != null)
        {
            page.Children.Add(Banner("", "Health data isn't available for this drive", d.Unavailable, "#F8FAFC", "#E2E8F0", "#334155"));
            return page;
        }
        if (d.Status is HealthStatus.Caution or HealthStatus.Bad)
        {
            bool bad = d.Status == HealthStatus.Bad;
            page.Children.Add(Banner(bad ? "" : "",
                bad ? "This drive may fail soon. Back it up now and plan to replace it." : "Keep an eye on this drive and make sure it's backed up.",
                string.Join("\n", d.Reasons.Select(r => "•  " + r)),
                bad ? "#FEF2F2" : "#FFFBEB", bad ? "#FECACA" : "#FDE68A", bad ? "#991B1B" : "#92400E"));
        }

        page.Children.Add(Tiles(d));
        page.Children.Add(d.Attributes.Count > 0 ? AttributeTable(d) : ReadingTable(d));
        return page;
    }

    private static FrameworkElement Summary(DriveHealth d, HealthSettings s)
    {
        var card = new Border
        {
            Background = Brushes.White, BorderBrush = Hex("#E2E8F0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Child = grid;

        // Health badge: status word and the percentage, CrystalDiskInfo style.
        var (soft, strong) = Colors(d.Status);
        var health = Badge("HEALTH STATUS", d.Status.ToString(),
            d.LifePercent is int p ? $"{p}%" : d.Kind == DriveKind.Hdd && d.Unavailable == null ? "no life % on hard disks" : null, soft, strong, 160);
        grid.Children.Add(health);

        int limit = d.Kind == DriveKind.Hdd ? s.HotHddC : s.HotSsdC;
        var (ts, tf) = TempColors(d.TemperatureC, limit);
        var temp = Badge("TEMPERATURE", d.TemperatureC is int t ? $"{t} °C" : "–", d.TemperatureC != null ? $"alert above {limit} °C" : null, ts, tf, 130);
        temp.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(temp, 1);
        grid.Children.Add(temp);

        var info = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = d.Model, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Hex("#0F172A"), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", new[] { $"Disk {d.DiskNumber}", d.KindText, Util.FormatBytes(d.Size), d.DriveLetters, d.IsSystem ? "Windows drive" : "" }.Where(x => x.Length > 0)),
            Foreground = Hex("#64748B"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 10),
        });
        var facts = new Grid();
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pairs = new (string, string)[]
        {
            ("Firmware", d.Firmware.Length > 0 ? d.Firmware : "–"),
            ("Serial number", d.Serial.Length > 0 ? d.Serial : "–"),
            ("Interface", d.Bus.Length > 0 ? d.Bus : "–"),
        };
        for (int i = 0; i < pairs.Length; i++)
        {
            facts.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = pairs[i].Item1, Foreground = Hex("#64748B"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
            var value = new TextBlock { Text = pairs[i].Item2, Foreground = Hex("#0F172A"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pairs[i].Item2, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            facts.Children.Add(label);
            facts.Children.Add(value);
        }
        info.Children.Add(facts);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);
        return card;
    }

    private static Border Badge(string caption, string big, string? small, string soft, string strong, double width)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = caption, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Hex(strong), Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = big, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Hex(strong), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        if (small != null)
            sp.Children.Add(new TextBlock
            {
                Text = small, FontSize = small.EndsWith('%') ? 20 : 11, FontWeight = small.EndsWith('%') ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Hex(strong), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
        return new Border { Width = width, MinHeight = 118, CornerRadius = new CornerRadius(12), Background = Hex(soft), Padding = new Thickness(10), Child = sp };
    }

    private static FrameworkElement Banner(string icon, string title, string body, string bg, string border, string fg)
    {
        var dock = new DockPanel();
        var ic = new TextBlock { Text = icon, FontFamily = Icons, FontSize = 18, Foreground = Hex(fg), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(ic, Dock.Left);
        dock.Children.Add(ic);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Hex(fg), TextWrapping = TextWrapping.Wrap });
        if (body.Length > 0) sp.Children.Add(new TextBlock { Text = body, Foreground = Hex(fg), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12.5 });
        dock.Children.Add(sp);
        return new Border
        {
            Background = Hex(bg), BorderBrush = Hex(border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 12), Child = dock,
        };
    }

    private static FrameworkElement Tiles(DriveHealth d)
    {
        var tiles = new List<(string Icon, string Label, string Value, string? Sub)>
        {
            ("", "Power-on time", d.PowerOnHours is long h ? $"{h:N0} hours" : "–", d.PowerOnHours is long h2 ? DaysYears(h2) : null),
            ("", "Power-on count", d.PowerCycles is long c ? $"{c:N0}" : "–", d.UnsafeShutdowns is long u ? $"{u:N0} unsafe shutdowns" : null),
        };
        if (d.Kind == DriveKind.Nvme)
        {
            tiles.Add(("", "Data written", d.DataWrittenBytes is long w ? Util.FormatBytes(w) : "–", null));
            tiles.Add(("", "Data read", d.DataReadBytes is long r ? Util.FormatBytes(r) : "–", null));
        }
        else
        {
            tiles.Add(("", "Bad sectors", d.BadSectors is long b ? $"{b:N0}" : "–", "replaced or unreadable"));
            tiles.Add(d.LifePercent is int p ? ("", "Life remaining", $"{p}%", "drive's wear indicator") : ("", "Type", d.KindText, null));
        }

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = tiles.Count, Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < tiles.Count; i++)
        {
            var (icon, label, value, sub) = tiles[i];
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = icon, FontFamily = Icons, FontSize = 14, Foreground = Hex("#2563EB"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Hex("#64748B"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Hex("#0F172A"), Margin = new Thickness(0, 6, 0, 0) });
            if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Hex("#64748B"), TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(new Border
            {
                Background = Brushes.White, BorderBrush = Hex("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(i == 0 ? 0 : 6, 0, i == tiles.Count - 1 ? 0 : 6, 0), Child = sp,
            });
        }
        return grid;
    }

    private static string DaysYears(long hours) => hours >= 24 * 365
        ? $"about {hours / 24.0 / 365:0.0} years"
        : $"about {hours / 24:N0} days";

    // ------------------------------------------------------------------ readings tables

    private static FrameworkElement AttributeTable(DriveHealth d)
    {
        var cols = new[] { 26.0, 46, -1, 76, 64, 96, 130 };
        var t = Table("SMART readings", new[] { "", "ID", "ATTRIBUTE", "CURRENT", "WORST", "THRESHOLD", "RAW VALUE" }, cols, out var rows);
        foreach (var a in d.Attributes)
            AddRow(rows, cols, a.Status, a.Id.ToString("X2"), a.Name, a.Current.ToString(), a.Worst.ToString(), a.Threshold.ToString(), a.RawHex);
        return t;
    }

    private static FrameworkElement ReadingTable(DriveHealth d)
    {
        var cols = new[] { 26.0, -1, 280 };
        var t = Table("NVMe health readings", new[] { "", "READING", "VALUE" }, cols, out var rows);
        foreach (var r in d.Readings) AddRow(rows, cols, r.Status, r.Name, r.Value);
        return t;
    }

    private static FrameworkElement Table(string title, string[] headers, double[] cols, out StackPanel rows)
    {
        var outer = new StackPanel();
        outer.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Hex("#0F172A"), Margin = new Thickness(0, 4, 0, 8) });
        rows = new StackPanel();
        var header = Row(cols, headers, isHeader: true);
        header.Background = Hex("#F8FAFC");
        header.BorderBrush = Hex("#E2E8F0");
        header.BorderThickness = new Thickness(0, 0, 0, 1);
        rows.Children.Add(header);
        outer.Children.Add(new Border
        {
            Background = Brushes.White, BorderBrush = Hex("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(1), Child = rows, ClipToBounds = true,
        });
        return outer;
    }

    private static void AddRow(StackPanel rows, double[] cols, HealthStatus status, params string[] cells)
    {
        var row = Row(cols, new[] { "" }.Concat(cells).ToArray(), isHeader: false);
        row.BorderBrush = Hex("#EEF2F6");
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        var grid = (Grid)row.Child;
        var dot = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Hex(Colors(status).Strong),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = status.ToString(),
        };
        grid.Children.Add(dot);
        if (status != HealthStatus.Good) row.Background = Hex(Colors(status).Soft);
        rows.Children.Add(row);
    }

    private static Border Row(double[] cols, string[] cells, bool isHeader)
    {
        var grid = new Grid { Height = isHeader ? 34 : 32 };
        foreach (var w in cols)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        for (int i = 0; i < cells.Length && i < cols.Length; i++)
        {
            if (cells[i].Length == 0) continue;
            bool raw = !isHeader && i == cols.Length - 1 && cols.Length == 7;
            var tb = new TextBlock
            {
                Text = cells[i], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 10, 0, 6, 0),
                FontSize = isHeader ? 11.5 : 12.5, FontWeight = isHeader ? FontWeights.SemiBold : i == 2 ? FontWeights.Medium : FontWeights.Normal,
                Foreground = Hex(isHeader ? "#64748B" : "#0F172A"), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (raw || (!isHeader && cols.Length == 7 && i == 1)) tb.FontFamily = Mono; // ID and raw value in hex
            Grid.SetColumn(tb, i);
            grid.Children.Add(tb);
        }
        return new Border { Child = grid, Padding = new Thickness(8, 0, 8, 0) };
    }
}
