using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskImager.Core;
using Microsoft.Win32;

namespace DiskImager;

/// <summary>
/// Pick the target drive for a restore or clone and decide how partitions are resized. The target drive is
/// drawn as a bar; resizable partitions have a handle on their right edge that can be dragged to grow or
/// shrink them (partitions after them move along).
/// </summary>
public partial class LayoutWindow : Window
{
    private const double GiB = 1024.0 * 1024 * 1024;
    private const double BarTop = 30, BarHeight = 78, MinPartPx = 52, GapPx = 3;

    private readonly SourceDescription _src;
    private readonly int? _sourceDisk;
    private LayoutPlan? _plan;
    private long[]? _custom;          // sizes in "Choose sizes myself" mode
    private int _selected = -1;       // partition shown in the "Selected" row
    private bool _settingMode;

    // Dragging a handle
    private int _drag = -1;
    private double _dragStartX, _dragBytesPerPx;
    private long _dragStartLen, _dragMax;

    public DiskInfo? Target { get; private set; }
    public LayoutPlan? Plan => _plan;
    public bool NewIds => NewIdsBox.IsChecked == true;
    public string? StagingFolder { get; private set; }

    private long StagingNeeded => _src.UsedBytes + (2L << 30);
    private bool Resizable(int i) => _src.ResizeBlockedReason == null && _src.Parts[i].CanGrow;

    public LayoutWindow(string heading, string explanation, SourceDescription src, List<DiskInfo> targets,
        DiskInfo? preselect, int? sourceDisk)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        Title = Heading.Text = heading;
        Explain.Text = explanation;
        _src = src;
        _sourceDisk = sourceDisk;

        SourceLabel.Text = $"Before (source) · {Util.FormatBytes(src.DiskSize)}";
        SourceBarHost.Content = DiskBars.Bar(src.DiskSize, src.Parts.Select(p =>
            new DiskBars.Segment(p.Offset, p.Length, p.Name, $"{Util.FormatBytes(p.Length)} ({Util.FormatBytes(p.UsedBytes)} used)",
                DiskBars.ColorFor(p.Type, p.HasLetter))), 40);

        foreach (var t in targets)
        {
            TargetBox.Items.Add(new ComboBoxItem
            {
                Content = $"Disk {t.Number}   {t.Model}   {Util.FormatBytes(t.Size)}" + (t.IsSystem ? "   (Windows is running from this disk)" : ""),
                IsEnabled = !t.IsSystem,
                Tag = t,
            });
        }

        if (src.ResizeBlockedReason != null)
        {
            AutoRadio.IsEnabled = CustomRadio.IsEnabled = false;
            KeepRadio.IsChecked = true;
        }

        // Start with the largest resizable partition selected (normally C:).
        _selected = Enumerable.Range(0, src.Parts.Count).Where(Resizable)
            .OrderByDescending(i => src.Parts[i].HasLetter).ThenByDescending(i => src.Parts[i].Length).DefaultIfEmpty(-1).First();

        int pre = targets.FindIndex(t => t.Number == preselect?.Number && !t.IsSystem);
        if (pre >= 0) TargetBox.SelectedIndex = pre;
        Recalculate();
    }

    private static string Gb(long bytes) => (bytes / GiB).ToString("#,0.00");

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _custom = null; // sizes chosen for another drive don't carry over
        Recalculate();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || TargetBox == null) return;
        // Switching to "Choose sizes myself" starts from whatever is shown now.
        if (CustomRadio.IsChecked == true && !_settingMode && _plan != null)
            _custom = _plan.Parts.Select(p => p.TargetLength).ToArray();
        Recalculate();
    }

    /// <summary>Puts the window in "Choose sizes myself" mode (used when a handle is dragged or a size typed).</summary>
    private void EnsureCustom()
    {
        if (CustomRadio.IsChecked == true && _custom != null) return;
        _custom = _plan!.Parts.Select(p => p.TargetLength).ToArray();
        _settingMode = true;
        CustomRadio.IsChecked = true;
        _settingMode = false;
    }

    private void Recalculate()
    {
        if (TargetBox == null || DriveCanvas == null) return;
        Target = (TargetBox.SelectedItem as ComboBoxItem)?.Tag as DiskInfo;
        if (Target == null)
        {
            _plan = null;
            DriveCanvas.Children.Clear();
            AfterLabel.Text = "After · choose a target drive";
            TargetSizeLabel.Text = "";
            SelectedPanel.Visibility = Visibility.Collapsed;
            ShowStatus("Choose the drive to write to.", warn: true);
            StagingGroup.Visibility = Visibility.Collapsed;
            return;
        }

        var plan = Layout.Original(_src, Target.Size, Target.SectorSize);
        if (AutoRadio.IsChecked == true) Layout.AutoFit(plan);
        else if (CustomRadio.IsChecked == true)
        {
            if (_custom == null || _custom.Length != plan.Parts.Count)
            {
                var start = Layout.Original(_src, Target.Size, Target.SectorSize);
                Layout.AutoFit(start);
                _custom = start.Parts.Select(p => p.TargetLength).ToArray();
            }
            for (int i = 0; i < plan.Parts.Count; i++) plan.Parts[i].TargetLength = _custom[i];
            Layout.Relayout(plan);
        }
        _plan = plan;

        bool anyResizable = Enumerable.Range(0, _src.Parts.Count).Any(Resizable);
        AfterLabel.Text = anyResizable ? "After · drag the handle on a partition's right edge to resize it" : "After (target drive)";
        TargetSizeLabel.Text = $"Target drive · {Util.FormatBytes(Target.Size)}";
        RenderDrive();
        UpdateSelectedPanel();
        UpdateStatus();
    }

    // ---------------------------------------------------------------- drawing the target drive

    private sealed record Item(int Part, long Start, long Length, double X, double W);

    private List<Item> _items = new();
    private double _bytesPerPx;

    /// <summary>
    /// Lays the drive out across the canvas: sizes are proportional, except that every partition gets at
    /// least <see cref="MinPartPx"/> so small ones (EFI, MSR, Recovery) stay visible and clickable.
    /// </summary>
    private void RenderDrive()
    {
        DriveCanvas.Children.Clear();
        _items.Clear();
        if (_plan == null || DriveCanvas.ActualWidth < 100) return;

        var raw = new List<(int Part, long Start, long Length)>();
        long pos = _plan.Parts.Count > 0 ? _plan.Parts.Min(p => p.TargetOffset) : 0;
        for (int i = 0; i < _plan.Parts.Count; i++)
        {
            var p = _plan.Parts[i];
            if (p.TargetOffset - pos > _plan.TargetSize / 100) raw.Add((-1, pos, p.TargetOffset - pos));
            raw.Add((i, p.TargetOffset, p.TargetLength));
            pos = Math.Max(pos, p.TargetEnd);
        }
        long free = _plan.TargetSize - pos;
        if (free > _plan.TargetSize / 400) raw.Add((-1, pos, free));

        double avail = DriveCanvas.ActualWidth - GapPx * (raw.Count - 1);
        var pinned = new bool[raw.Count];
        double scale = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            double fixedPx = 0; long flexBytes = 0;
            for (int k = 0; k < raw.Count; k++)
                if (pinned[k]) fixedPx += MinPartPx; else flexBytes += raw[k].Length;
            scale = flexBytes > 0 ? Math.Max(0, avail - fixedPx) / flexBytes : 0;
            bool changed = false;
            for (int k = 0; k < raw.Count; k++)
                if (!pinned[k] && raw[k].Part >= 0 && raw[k].Length * scale < MinPartPx) { pinned[k] = true; changed = true; }
            if (!changed) break;
        }
        _bytesPerPx = scale > 0 ? 1 / scale : 0;

        double x = 0;
        for (int k = 0; k < raw.Count; k++)
        {
            double w = pinned[k] ? MinPartPx : raw[k].Length * scale;
            _items.Add(new Item(raw[k].Part, raw[k].Start, raw[k].Length, x, w));
            x += w + GapPx;
        }

        foreach (var it in _items) DrawItem(it);
        foreach (var it in _items.Where(i => i.Part >= 0 && Resizable(i.Part))) DrawHandle(it);
    }

    private void DrawItem(Item it)
    {
        if (it.Part < 0)
        {
            var free = new Border
            {
                Width = it.W, Height = BarHeight, CornerRadius = new CornerRadius(7),
                BorderBrush = DiskBars.Hex("#CBD5E1"), BorderThickness = new Thickness(1.5), Padding = new Thickness(9, 6, 6, 6),
                Background = new LinearGradientBrush(Color.FromRgb(0xF1, 0xF5, 0xF9), Color.FromRgb(0xE2, 0xE8, 0xF0), 45)
                {
                    SpreadMethod = GradientSpreadMethod.Repeat, MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(0, 0), EndPoint = new Point(8, 8),
                },
                Child = Labels("Unallocated", Util.FormatBytes(it.Length), null, DiskBars.Hex("#475569")),
                ToolTip = $"Unallocated\n{Util.FormatBytes(it.Length)}",
            };
            Place(free, it.X, BarTop);
            return;
        }

        var pp = _plan!.Parts[it.Part];
        var sp = pp.Source;
        string change = pp.Grows ? "  ▲" : pp.Shrinks ? "  ▼" : "";
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(Labels(sp.Name, Util.FormatBytes(pp.TargetLength) + change,
            it.W > 150 && sp.UsedBytes > 0 && sp.CanGrow ? $"{Util.FormatBytes(sp.UsedBytes)} used" : null, Brushes.White));
        if (sp.CanGrow && sp.UsedBytes > 0)
        {
            // Lighter strip along the bottom: how much of the partition is data.
            grid.Children.Add(new Border
            {
                Height = 6, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, it.W * Math.Min(1, (double)sp.UsedBytes / pp.TargetLength)),
                Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(-9, 0, 0, -6),
            });
        }
        var seg = new Border
        {
            Width = it.W, Height = BarHeight, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 6, 6, 6),
            Background = DiskBars.Hex(DiskBars.ColorFor(sp.Type, sp.HasLetter)), Child = grid, Cursor = Cursors.Hand,
            ToolTip = $"{sp.Name}\n{(sp.FileSystem.Length > 0 ? sp.FileSystem : sp.Type)} · {Util.FormatBytes(sp.UsedBytes)} used\n" +
                      $"Original {Util.FormatBytes(sp.Length)} → new {Util.FormatBytes(pp.TargetLength)}" +
                      (Resizable(it.Part) ? "" : "\nFixed size (can't be resized)"),
            BorderBrush = it.Part == _selected ? DiskBars.Hex("#93C5FD") : null,
            BorderThickness = it.Part == _selected ? new Thickness(2.5) : new Thickness(0),
        };
        int part = it.Part;
        seg.MouseLeftButtonDown += (_, e) => { _selected = part; RenderDrive(); UpdateSelectedPanel(); e.Handled = true; };
        Place(seg, it.X, BarTop);

        // Smallest allowed size of the selected partition: red dashed line.
        if (it.Part == _selected && Resizable(it.Part) && sp.CanShrink && pp.MinLength < pp.TargetLength)
        {
            double mx = it.X + it.W * pp.MinLength / pp.TargetLength;
            var line = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = BarHeight + 12, Stroke = DiskBars.Hex("#DC2626"), StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
            };
            Place(line, mx, BarTop - 6);
            var lbl = new TextBlock { Text = $"can't go below {Util.FormatBytes(pp.MinLength)}", FontSize = 11, Foreground = DiskBars.Hex("#DC2626"), IsHitTestVisible = false };
            Place(lbl, Math.Max(0, mx - 60), BarTop + BarHeight + 6);
        }
    }

    private static StackPanel Labels(string title, string size, string? extra, Brush fg)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = fg, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (extra != null) sp.Children.Add(new TextBlock { Text = extra, Foreground = fg, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.9 });
        sp.Children.Add(new TextBlock { Text = size, Foreground = fg, FontSize = extra != null ? 17 : 11.5, FontWeight = extra != null ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, extra != null ? 4 : 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return sp;
    }

    private void DrawHandle(Item it)
    {
        bool active = it.Part == _drag || (_drag < 0 && it.Part == _selected);
        var grips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int g = 0; g < 2; g++)
            grips.Children.Add(new Border { Width = 2, Height = 18, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(1),
                Background = DiskBars.Hex(active ? "#93C5FD" : "#CBD5E1") });
        var handle = new HandleBorder
        {
            Width = 14, Height = 52, CornerRadius = new CornerRadius(7), Background = Brushes.White,
            BorderBrush = DiskBars.Hex(active ? "#2563EB" : "#94A3B8"), BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeWE, Child = grips,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.25 },
            ToolTip = _drag >= 0 ? null : "Drag to resize",
        };
        AutomationPropertiesHelper.SetName(handle, $"Resize {_src.Parts[it.Part].Name}");
        int part = it.Part;
        handle.MouseLeftButtonDown += (_, e) => StartDrag(part, e);
        Place(handle, it.X + it.W - 7, BarTop + (BarHeight - 52) / 2);

        if (active && (_drag >= 0 || it.Part == _selected))
        {
            var pp = _plan!.Parts[it.Part];
            long delta = pp.TargetLength - pp.Source.Length;
            string d = delta == 0 ? "" : $"  ({(delta > 0 ? "+" : "−")}{Util.FormatBytes(Math.Abs(delta))})";
            var tip = new Border
            {
                Background = DiskBars.Hex("#0F172A"), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 4, 9, 4),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = $"{pp.Source.Name.Split("  ")[0]}  {Util.FormatBytes(pp.TargetLength)}{d}", Foreground = Brushes.White, FontSize = 12 },
            };
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tx = Math.Clamp(it.X + it.W - tip.DesiredSize.Width / 2, 0, Math.Max(0, DriveCanvas.ActualWidth - tip.DesiredSize.Width));
            Place(tip, tx, 0);
        }
    }

    private void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        DriveCanvas.Children.Add(e);
    }

    private void DriveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderDrive();

    // ---------------------------------------------------------------- dragging

    private void StartDrag(int part, MouseButtonEventArgs e)
    {
        if (_plan == null) return;
        EnsureCustom();
        var pp = _plan.Parts[part];
        _drag = part;
        _selected = part;
        _dragStartX = e.GetPosition(DriveCanvas).X;
        _dragStartLen = pp.TargetLength;
        _dragMax = pp.TargetLength + Math.Max(0, _plan.UsableEnd - _plan.End);
        _dragBytesPerPx = _bytesPerPx;
        DriveCanvas.CaptureMouse();
        e.Handled = true;
        RenderDrive();
    }

    private void DriveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag < 0 || _plan == null || _custom == null) return;
        double dx = e.GetPosition(DriveCanvas).X - _dragStartX;
        var src = _src.Parts[_drag];
        long min = src.CanShrink ? _plan.Parts[_drag].MinLength : src.Length;
        long len = (long)(_dragStartLen + dx * _dragBytesPerPx);
        // Snap to whole GB on big partitions (to 1 MB on small ones) so sizes come out as round numbers.
        long snap = _dragStartLen > 20 * (long)GiB ? (long)GiB : Layout.MiB;
        len = (long)Math.Round((double)len / snap) * snap;
        len = Math.Clamp(len, min, Math.Max(min, _dragMax));
        len = Layout.AlignDown(len, Layout.MiB);
        if (len == _custom[_drag]) return;
        _custom[_drag] = len;
        Recalculate();
    }

    private void DriveCanvas_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag();
    private void DriveCanvas_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_drag < 0) return;
        _drag = -1;
        if (DriveCanvas.IsMouseCaptured) DriveCanvas.ReleaseMouseCapture();
        Recalculate();
    }

    // ---------------------------------------------------------------- selected partition row

    private void UpdateSelectedPanel()
    {
        if (_plan == null || _selected < 0 || _selected >= _plan.Parts.Count)
        {
            SelectedPanel.Visibility = _plan != null && _src.Parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedName.Text = "";
            SelectedEdit.Visibility = Visibility.Collapsed;
            SelectedHint.Text = _src.ResizeBlockedReason ?? "Click a partition on the drive to see or change its size.";
            return;
        }
        SelectedPanel.Visibility = Visibility.Visible;
        var pp = _plan.Parts[_selected];
        SelectedName.Text = pp.Source.Name;
        if (!Resizable(_selected))
        {
            SelectedEdit.Visibility = Visibility.Collapsed;
            SelectedHint.Text = $"{Util.FormatBytes(pp.TargetLength)} · fixed size ({(pp.Source.FileSystem.Length > 0 ? pp.Source.FileSystem : pp.Source.Type)} partitions can't be resized)";
            return;
        }
        SelectedEdit.Visibility = Visibility.Visible;
        if (!SizeBox.IsKeyboardFocused) SizeBox.Text = Gb(pp.TargetLength);
        long max = pp.TargetLength + Math.Max(0, _plan.UsableEnd - _plan.End);
        long min = pp.Source.CanShrink ? pp.MinLength : pp.Source.Length;
        FillButton.IsEnabled = max - pp.TargetLength >= Layout.MiB;
        SelectedHint.Text = $"· {Gb(min)} – {Gb(max)} GB · type an exact size, or drag on the drive above";
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySizeBox(); e.Handled = true; }
    }

    private void SizeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplySizeBox();

    private void ApplySizeBox()
    {
        if (_plan == null || _selected < 0 || !Resizable(_selected)) return;
        var pp = _plan.Parts[_selected];
        if (!double.TryParse(SizeBox.Text.Replace(",", "").Trim(), out double gb) || gb <= 0)
        {
            SizeBox.Text = Gb(pp.TargetLength);
            return;
        }
        long min = pp.Source.CanShrink ? pp.MinLength : pp.Source.Length;
        long max = pp.TargetLength + Math.Max(0, _plan.UsableEnd - _plan.End);
        long len = Layout.AlignDown(Math.Clamp((long)(gb * GiB), min, Math.Max(min, max)), Layout.MiB);
        if (len == pp.TargetLength) { SizeBox.Text = Gb(len); return; }
        EnsureCustom();
        _custom![_selected] = len;
        Recalculate();
        SizeBox.Text = Gb(_plan!.Parts[_selected].TargetLength);
    }

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _selected < 0 || !Resizable(_selected)) return;
        EnsureCustom();
        var pp = _plan.Parts[_selected];
        _custom![_selected] = Layout.AlignDown(pp.TargetLength + Math.Max(0, _plan.UsableEnd - _plan.End), Layout.MiB);
        Recalculate();
        // Relayout may need a little slack for alignment of the partitions that follow.
        if (_plan!.End > _plan.UsableEnd)
        {
            _custom[_selected] -= Layout.AlignUp(_plan.End - _plan.UsableEnd, Layout.MiB);
            Recalculate();
        }
    }

    // ---------------------------------------------------------------- status, staging, OK

    private void UpdateStatus()
    {
        if (_plan == null || Target == null) return;
        string? error = _plan.Validate();
        if (error != null)
        {
            ShowStatus(error, warn: true);
            StagingGroup.Visibility = Visibility.Collapsed;
            return;
        }

        long spare = _plan.UsableEnd - _plan.End;
        var lines = new List<string>();
        foreach (var p in _plan.Parts.Where(p => p.Grows || p.Shrinks))
            lines.Add($"{p.Source.Name} will {(p.Grows ? "grow" : "shrink")} from {Util.FormatBytes(p.Source.Length)} to {Util.FormatBytes(p.TargetLength)}.");
        int moved = _plan.Parts.Count(p => p.TargetOffset != p.Source.Offset);
        if (moved > 0) lines.Add($"{moved} partition(s) will be moved to make room.");
        if (_plan.IsIdentity) lines.Add("Partitions are copied with their original sizes.");
        if (spare > (1L << 30)) lines.Add($"{Util.FormatBytes(spare)} will be left unallocated at the end of the drive.");
        ShowStatus(string.Join("\n", lines), warn: false);

        if (_plan.NeedsShrink)
        {
            StagingGroup.Visibility = Visibility.Visible;
            StagingNote.Text = $"To shrink safely, a temporary copy of the disk (about {Util.FormatBytes(StagingNeeded)}) is made in this folder, " +
                               "Windows shrinks it there, and then it's written to the target. Your source is never changed. This takes roughly twice as long. " +
                               "Pick a folder on a drive other than the target.";
            if (StagingBox.Text.Length == 0)
                StagingBox.Text = DiskCopy.SuggestStagingFolder(Target.Number, _sourceDisk, StagingNeeded) ?? "";
        }
        else StagingGroup.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text, bool warn)
    {
        PlanStatus.Text = text;
        PlanStatus.Foreground = warn ? DiskBars.Hex("#991B1B") : DiskBars.Hex("#14532D");
        StatusBorder.Background = warn ? DiskBars.Hex("#FEF2F2") : DiskBars.Hex("#F0FDF4");
    }

    private void BrowseStaging_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder for the temporary working copy" };
        if (dlg.ShowDialog(this) == true) StagingBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Target == null || _plan == null) { Warn("Choose the drive to write to."); return; }
        string? error = _plan.Validate();
        if (error != null) { Warn(error); return; }

        if (_plan.NeedsShrink)
        {
            string folder = StagingBox.Text.Trim();
            if (folder.Length < 3 || folder[1] != ':') { Warn("Choose a local folder (like D:\\Temp) for the temporary working copy."); return; }
            if (DiskEnumerator.DiskNumberOfPath(folder) == Target.Number) { Warn("The working copy can't be on the target drive."); return; }
            try
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(folder)!);
                if (drive.AvailableFreeSpace < StagingNeeded)
                {
                    Warn($"{drive.Name} only has {Util.FormatBytes(drive.AvailableFreeSpace)} free; the working copy needs about {Util.FormatBytes(StagingNeeded)}.");
                    return;
                }
            }
            catch (Exception ex) { Warn(ex.Message); return; }
            StagingFolder = folder;
        }

        if (ConfirmBox.Text.Trim() != Target.Number.ToString())
        {
            Warn($"Type {Target.Number} in the confirmation box to erase Disk {Target.Number}.");
            return;
        }
        DialogResult = true;
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>A resize handle that screen readers and UI automation can find (a plain Border is invisible to them).</summary>
internal sealed class HandleBorder : Border
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
}

/// <summary>Gives code-built elements a name that screen readers and UI automation can use.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);
}
