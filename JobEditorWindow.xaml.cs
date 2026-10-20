using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskImager.Core;
using Microsoft.Win32;

namespace DiskImager;

/// <summary>
/// New / edit scheduled backup, and "Image disk" (one-off), as steps: disk, location, folders to leave out,
/// encryption, schedule (or backup type), old backups, review. Each step is checked on Next; Save checks them all.
/// Editing an existing job opens on the review step.
/// </summary>
public partial class JobEditorWindow : Window
{
    private readonly List<DiskInfo> _disks;
    private readonly bool _oneOff;
    private readonly List<Step> _steps = new();
    private readonly List<string> _exclude = new();
    private int _current = -1;
    private int _furthest;
    private bool _switching;

    public BackupJob Job { get; }
    public bool OneOffIncremental => OneOffIncr.IsChecked == true;

    private sealed record Step(string Name, string Icon, FrameworkElement Page, Action Check, bool Optional = false)
    {
        public ListBoxItem? Item { get; set; }
        public TextBlock? IconText { get; set; }
    }

    private static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive") is { Length: 2 } d ? d : "C:";

    /// <summary>One-click folders to leave out (each button may cover several patterns).</summary>
    private static readonly (string Label, string Icon, string[] Patterns)[] QuickFolders =
    {
        ("Downloads", "\uE896", new[] { @"\Users\*\Downloads" }),
        ("Temporary files", "\uE823", new[] { @"\Users\*\AppData\Local\Temp", @"\Windows\Temp" }),
        ("Recycle Bin", "\uE74D", new[] { @"\$Recycle.Bin" }),
        ("Browser caches", "\uE774", new[]
        {
            @"\Users\*\AppData\Local\Google\Chrome\User Data\*\Cache",
            @"\Users\*\AppData\Local\Microsoft\Edge\User Data\*\Cache",
            @"\Users\*\AppData\Local\Mozilla\Firefox\Profiles\*\cache2",
        }),
        ("Videos", "\uE714", new[] { @"\Users\*\Videos" }),
        ("Music", "\uE8D6", new[] { @"\Users\*\Music" }),
        ("Pictures", "\uEB9F", new[] { @"\Users\*\Pictures" }),
        ("OneDrive (already in the cloud)", "\uE753", new[] { @"\Users\*\OneDrive" }),
    };

    public JobEditorWindow(List<DiskInfo> disks, BackupJob? job, bool oneOff, DiskInfo? preselect)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        _disks = disks;
        _oneOff = oneOff;
        Job = job ?? new BackupJob();

        // Disks shown as cards, like the Disks page. Index in the list = index in _disks
        // (an extra "not connected" card is added last for a job whose disk is unplugged).
        foreach (var d in disks) DiskList.Items.Add(DiskBars.Card(d, compact: true));
        int idx = preselect != null ? disks.FindIndex(d => d.Number == preselect.Number) : disks.FindIndex(d => d.Id == Job.SourceDiskId);
        if (idx < 0 && job != null)
        {
            DiskList.Items.Add(NotConnectedCard(Job.SourceDiskDescription));
            idx = DiskList.Items.Count - 1;
        }
        DiskList.SelectedIndex = Math.Max(idx, 0);

        if (job == null && preselect != null)
            Job.Name = oneOff ? $"Disk {preselect.Number} image" : $"{Environment.MachineName} Disk {preselect.Number}";
        NameBox.Text = Job.Name;
        DestBox.Text = Job.Destination;
        UserBox.Text = Job.NetUser ?? "";
        CompressionBox.SelectedIndex = (int)Job.Compression;
        FullSchedule.Load(Job.Full);
        IncrSchedule.Load(Job.Incremental);
        AfterBox.SelectedIndex = Job.After is PostAction.Shutdown or PostAction.Restart ? (int)Job.After : 0;
        WakeBox.IsChecked = Job.WakeToRun;
        VerifyBox.IsChecked = Job.VerifyAfter;
        TrackBox.IsChecked = Job.ChangeTracking;
        NotifyBox.SelectedIndex = (int)Job.Notify;
        NotifyHint.Text = AppSettings.Load().Email.Enabled ? "" : "Set up email under Settings first.";
        _exclude.AddRange(Job.ExcludeFolders);
        KeepBox.Text = Job.KeepFullSets.ToString();
        MaxIncrBox.Text = Job.MaxIncrementals.ToString();
        if (!string.IsNullOrEmpty(Job.NetPasswordProtected))
            PassBox.ToolTip = "A password is saved. Leave empty to keep it.";
        InputBehaviors.AttachNumber(KeepBox, 3);
        InputBehaviors.AttachNumber(MaxIncrBox, 3);
        UpdateNetworkLogin();

        EncryptChoice.Items.Add(ChoiceCard("\uE785", "No encryption", "Fine for a NAS or drive only you and your team can reach", "#F1F5F9", "#475569"));
        EncryptChoice.Items.Add(ChoiceCard("\uE72E", "Encrypt with a password (AES-256)", "Recommended for USB drives that leave the office", "#DBEAFE", "#1D4ED8"));
        EncryptChoice.SelectedIndex = Job.EncryptionPasswordProtected != null ? 1 : 0;
        if (Job.EncryptionPasswordProtected != null)
            EncPass1.ToolTip = EncPass2.ToolTip = "A password is saved. Leave both boxes empty to keep it.";

        foreach (var (label, icon, patterns) in QuickFolders)
        {
            var b = new Button { Content = label, Tag = icon, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => ToggleQuick(patterns);
            b.DataContext = patterns;
            QuickPanel.Children.Add(b);
        }
        DrawExcludes();

        // The steps.
        _steps.Add(new Step("Disk", "\uEDA2", DiskPage, CheckDisk));
        _steps.Add(new Step("Location", "\uE8B7", LocationPage, () => CheckLocation()));
        _steps.Add(new Step("Exclude folders", "\uE8F8", ExcludePage, CheckExcludes, Optional: true));
        _steps.Add(new Step("Encryption", "\uE72E", EncryptPage, () => ReadEncryption(), Optional: true));
        if (oneOff) _steps.Add(new Step("Backup type", "\uE81C", TypePage, () => { }));
        else _steps.Add(new Step("Schedule", "\uE787", SchedulePage, CheckSchedule));
        _steps.Add(new Step("Old backups", "\uE81C", RetentionPage, () => CheckRetention()));
        _steps.Add(new Step("Review", "\uE9D5", ReviewPage, () => { }));
        foreach (var s in _steps)
        {
            var icon = new TextBlock { Text = s.Icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15,
                Foreground = DiskBars.Hex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center, Width = 18 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = s.Name, FontSize = 13.5, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            s.Item = new ListBoxItem { Content = row };
            System.Windows.Automation.AutomationProperties.SetName(s.Item, s.Name);
            s.IconText = icon;
            StepList.Items.Add(s.Item);
        }

        if (oneOff)
        {
            Title = "Image a disk now";
            RailTitle.Text = "Image a disk";
        }
        else
        {
            Title = job == null ? "New scheduled backup" : $"Edit '{job.Name}'";
            RailTitle.Text = job == null ? "New backup job" : job.Name;
        }
        FullSchedule.Changed += UpdateClashes;
        IncrSchedule.Changed += UpdateClashes;

        // Editing: everything is filled in already, so open on the summary.
        bool editing = job != null && !oneOff;
        _furthest = editing ? _steps.Count - 1 : 0;
        Loaded += (_, _) => Show(editing ? _steps.Count - 1 : 0);
    }

    // ------------------------------------------------------------------ navigation

    private void Show(int i)
    {
        i = Math.Clamp(i, 0, _steps.Count - 1);
        _current = i;
        _furthest = Math.Max(_furthest, i);
        for (int k = 0; k < _steps.Count; k++)
        {
            var s = _steps[k];
            s.Page.Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            bool done = k < _furthest && k != i;
            s.IconText!.Text = done ? "\uE73E" : s.Icon;
            s.IconText.Foreground = DiskBars.Hex(k == i ? "#FFFFFF" : done ? "#22C55E" : "#94A3B8");
            ((TextBlock)((StackPanel)s.Item!.Content).Children[1]).Foreground = DiskBars.Hex(k == i ? "#FFFFFF" : "#CBD5E1");
            ((TextBlock)((StackPanel)s.Item.Content).Children[1]).FontWeight = k == i ? FontWeights.SemiBold : FontWeights.Normal;
        }
        foreach (var p in new FrameworkElement[] { TypePage, SchedulePage })
            if (!_steps.Any(s => s.Page == p)) p.Visibility = Visibility.Collapsed;
        _switching = true;
        StepList.SelectedIndex = i;
        _switching = false;

        bool last = i == _steps.Count - 1;
        BackButton.Visibility = i == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = _steps[i].Optional ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = last ? (_oneOff ? "Start backup" : "Save job") : "Next";
        OkButton.Tag = last ? (_oneOff ? "\uE768" : "\uE74E") : "\uE72A";
        StepText.Text = $"Step {i + 1} of {_steps.Count}";
        if (last) FillReview();
        if (_steps[i].Page == SchedulePage) _ = LoadOthers(force: false);
        PageScroll.ScrollToTop();
        Dispatcher.BeginInvoke(() => FocusFirst(_steps[i].Page), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void FocusFirst(FrameworkElement page)
    {
        if (page.FindName("NameBox") is TextBox name && name.IsVisible && page.IsAncestorOf(name)) { name.Focus(); return; }
        page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || StepList.SelectedIndex < 0 || StepList.SelectedIndex == _current) return;
        Show(StepList.SelectedIndex);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(_current - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => Show(_current + 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_current == _steps.Count - 1) { Save(); return; }
        try { _steps[_current].Check(); }
        catch (ArgumentException ex) { Warn(ex.Message); return; }
        Show(_current + 1);
    }

    /// <summary>The disk list sits inside the page's scroll area: let the mouse wheel scroll the page.</summary>
    private void Inner_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta / 3.0);
    }

    private void Warn(string message) => AppMessage.Show(this, message, "Check the details", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ------------------------------------------------------------------ step checks

    private void CheckDisk()
    {
        if (DiskList.SelectedIndex < 0) throw new ArgumentException("Choose the disk to back up.");
        if (NameBox.Text.Trim().Length == 0) throw new ArgumentException("Give the job a name.");
    }

    private string CheckLocation()
    {
        string dest = DestBox.Text.Trim().TrimEnd('\\');
        if (dest.Length < 2) throw new ArgumentException("Choose where to save the images.");
        if (NetworkShare.IsNetworkPath(dest)) NetworkShare.ShareRoot(dest);
        else if (!(dest.Length >= 2 && dest[1] == ':')) throw new ArgumentException("Use a full path like E:\\Backups or \\\\NAS\\Backups.");
        return dest;
    }

    private void CheckExcludes()
    {
        foreach (var x in _exclude)
            if (Exclusions.Validate(x) is string why) throw new ArgumentException(why);
    }

    private void CheckSchedule()
    {
        FullSchedule.Read("Full backup");
        IncrSchedule.Read("Incremental backup");
    }

    private (int Keep, int MaxIncr) CheckRetention()
    {
        if (!int.TryParse(KeepBox.Text, out int keep) || keep < 0) throw new ArgumentException("Enter how many full backups to keep (0 = keep everything).");
        if (!int.TryParse(MaxIncrBox.Text, out int maxIncr) || maxIncr < 0) throw new ArgumentException("Enter after how many incrementals to start a fresh full backup (0 = only on the full schedule).");
        return (keep, maxIncr);
    }

    // ------------------------------------------------------------------ disk, location

    private static FrameworkElement NotConnectedCard(string description) => new Border
    {
        Background = DiskBars.Hex("#F8FAFC"),
        BorderBrush = DiskBars.Hex("#CBD5E1"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = description, FontWeight = FontWeights.SemiBold, Foreground = DiskBars.Hex("#475569") },
                new TextBlock
                {
                    Text = "Not connected right now. The job keeps this disk; backups run when it's plugged in.",
                    Foreground = DiskBars.Hex("#64748B"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                },
            },
        },
    };

    private void DestBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateNetworkLogin();

    /// <summary>The network login only applies to \\server\share destinations.</summary>
    private void UpdateNetworkLogin()
    {
        if (NetPanel == null) return;
        NetPanel.Visibility = NetworkShare.IsNetworkPath(DestBox.Text.Trim()) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should images be saved?" };
        if (dlg.ShowDialog(this) == true) DestBox.Text = dlg.FolderName;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        string dest = DestBox.Text.Trim();
        bool net = NetworkShare.IsNetworkPath(dest);
        string user = net ? UserBox.Text.Trim() : "";
        string? pass = !net ? null : PassBox.Password.Length > 0 ? PassBox.Password : JobStore.Unprotect(Job.NetPasswordProtected);
        if (dest.Length == 0) return;
        var disk = DiskList.SelectedIndex >= 0 && DiskList.SelectedIndex < _disks.Count ? _disks[DiskList.SelectedIndex] : null;
        string jobName = NameBox.Text.Trim();
        try
        {
            string space = await Task.Run(() =>
            {
                NetworkShare.Test(dest, user, pass);
                long free = BackupEngine.FreeSpaceProvider(dest);
                if (free < 0) return "";
                string s = $"\n\n{Util.FormatBytes(free)} free there.";
                if (disk == null) return s;
                long used = SourceDescription.FromDisk(disk).UsedBytes;
                var images = ImageCatalog.Scan(dest).Where(i => string.Equals(i.Header.JobName, jobName, StringComparison.OrdinalIgnoreCase));
                long need = BackupEngine.EstimateSize(true, used, null, images);
                s += $" A full backup of {disk.Title} needs up to about {Util.FormatBytes(need)}.";
                if (free < need) s += "\n\n⚠ That's not enough space for a full backup.";
                return s;
            });
            AppMessage.Show(this, "DiskImager can write there." + space, "Destination test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppMessage.Show(this, "DiskImager can't write to that location.\n\n" + ex.Message, "Destination test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ folders to leave out

    private static string[] Full(string[] patterns) => patterns.Select(p => SystemDrive + p).ToArray();

    private bool Has(string pattern) => _exclude.Contains(pattern, StringComparer.OrdinalIgnoreCase);

    private void ToggleQuick(string[] patterns)
    {
        var full = Full(patterns);
        if (full.All(Has)) _exclude.RemoveAll(x => full.Contains(x, StringComparer.OrdinalIgnoreCase));
        else foreach (var p in full.Where(p => !Has(p))) _exclude.Add(p);
        DrawExcludes();
    }

    private void AddExclude(string path)
    {
        string p = path.Trim().TrimEnd('\\');
        if (p.Length == 0) return;
        if (Exclusions.Validate(p) is string why) { Warn(why); return; }
        if (!Has(p)) _exclude.Add(p);
        ExcludePathBox.Clear();
        DrawExcludes();
    }

    private void AddExclude_Click(object sender, RoutedEventArgs e) => AddExclude(ExcludePathBox.Text);

    private void ExcludePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // Enter adds the folder instead of going to the next step
        AddExclude(ExcludePathBox.Text);
    }

    private void BrowseExclude_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder to leave out of the backup" };
        if (dlg.ShowDialog(this) == true) AddExclude(dlg.FolderName);
    }

    private void DrawExcludes()
    {
        foreach (Button b in QuickPanel.Children)
        {
            if (b.DataContext is string[] pats && Full(pats).All(Has)) b.Style = (Style)FindResource("ChipOn");
            else b.ClearValue(StyleProperty); // back to the app's normal button look
        }

        ExcludeCount.Text = _exclude.Count == 0 ? "Left out" : $"Left out ({_exclude.Count})";
        ExcludeList.Children.Clear();
        if (_exclude.Count == 0)
        {
            ExcludeList.Children.Add(new TextBlock
            {
                Text = "Nothing is left out: everything on the disk is backed up.", Style = (Style)FindResource("Hint"), Margin = new Thickness(16, 0, 16, 8),
            });
            return;
        }
        foreach (var path in _exclude.ToList())
        {
            var remove = new Button
            {
                Content = "Remove", Tag = "\uE711", Foreground = (Brush)FindResource("DangerBrush"), Padding = new Thickness(10, 3, 10, 3),
                MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Back this folder up again",
            };
            remove.Click += (_, _) => { _exclude.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)); DrawExcludes(); };
            DockPanel.SetDock(remove, Dock.Right);
            var row = new DockPanel { Margin = new Thickness(16, 6, 12, 6) };
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = path, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path,
            });
            ExcludeList.Children.Add(new Border { BorderBrush = DiskBars.Hex("#EEF2F6"), BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
        }
    }

    // ------------------------------------------------------------------ encryption

    private static FrameworkElement ChoiceCard(string icon, string title, string sub, string tileBg, string tileFg)
    {
        var dock = new DockPanel();
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = DiskBars.Hex(tileBg), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = DiskBars.Hex(tileFg),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        dock.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = DiskBars.Hex("#0F172A") });
        text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = DiskBars.Hex("#64748B"), Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(text);
        var card = new Border
        {
            Background = Brushes.White, BorderBrush = DiskBars.Hex("#E2E8F0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Child = dock,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, title);
        return card;
    }

    private bool Encrypting => EncryptChoice.SelectedIndex == 1;

    private void EncryptChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EncryptPanel == null) return;
        EncryptPanel.IsEnabled = Encrypting;
        EncryptPanel.Opacity = Encrypting ? 1 : 0.5;
    }

    /// <summary>The protected password to save, or null when encryption is off. Throws if the boxes don't agree.</summary>
    private string? ReadEncryption()
    {
        if (!Encrypting) return null;
        string a = EncPass1.Password, b = EncPass2.Password;
        if (a.Length == 0 && b.Length == 0 && Job.EncryptionPasswordProtected != null) return Job.EncryptionPasswordProtected;
        if (a.Length < 8) throw new ArgumentException("Choose a backup password of at least 8 characters.");
        if (a != b) throw new ArgumentException("The two backup passwords don't match.");
        return JobStore.Protect(a);
    }

    // ------------------------------------------------------------------ other PCs' backup times

    private List<ScheduleClash.Run>? _others;
    private string? _othersFor;
    private List<ScheduleClash.Clash> _clashes = new();

    private async void RefreshOthers_Click(object sender, RoutedEventArgs e) => await LoadOthers(force: true);

    /// <summary>Reads the other PCs' backup times from the status notes at the top of this job's location.</summary>
    private async Task LoadOthers(bool force)
    {
        string dest = DestBox.Text.Trim().TrimEnd('\\');
        if (!force && dest == _othersFor) { UpdateClashes(); return; }
        _othersFor = dest;
        _others = null;
        OthersList.Children.Clear();
        ClashPanel.Children.Clear();
        if (dest.Length < 3)
        {
            OthersWhere.Text = "";
            OthersHint.Text = "Choose where the backups go first (the Location step).";
            return;
        }
        string root = Path.GetDirectoryName(WeeklyReport.StatusFolder(dest)) ?? dest;
        OthersWhere.Text = $"Backup times other PCs shared in {root}";
        OthersHint.Text = "Checking other PCs' backup times…";
        bool net = NetworkShare.IsNetworkPath(dest);
        string user = net ? UserBox.Text.Trim() : "";
        string? pass = !net ? null : PassBox.Password.Length > 0 ? PassBox.Password : JobStore.Unprotect(Job.NetPasswordProtected);
        string jobName = Job.Name.Length > 0 && Job.RegisteredTasks.Count > 0 ? Job.Name : NameBox.Text.Trim();
        try
        {
            var others = await Task.Run(() =>
            {
                if (net) NetworkShare.Connect(dest, user.Length > 0 ? user : null, pass, null);
                return ScheduleClash.Others(dest, WeeklyReport.EnsureMachineId().MachineId, jobName);
            });
            if (dest != _othersFor) return; // the location changed while reading
            _others = others;
            UpdateClashes();
        }
        catch (Exception ex)
        {
            if (dest != _othersFor) return;
            OthersHint.Text = $"Couldn't read other PCs' backup times from {root}: {ex.Message}";
        }
    }

    private void UpdateClashes()
    {
        if (_others == null || SchedulePage == null) return;
        var full = FullSchedule.Peek();
        var incr = IncrSchedule.Peek();
        _clashes = ScheduleClash.Find(full, Job.LastFullMinutes, incr, Job.LastIncrementalMinutes, _others, DateTime.Now);

        // Overlaps, with a suggested free time for each schedule that clashes.
        ClashPanel.Children.Clear();
        if (_others.Count > 0)
        {
            bool bad = _clashes.Count > 0;
            var box = new StackPanel();
            box.Children.Add(new TextBlock
            {
                Text = bad ? "This job would back up at the same time as:" : "No overlaps: this job runs when no other backup here does.",
                FontWeight = FontWeights.SemiBold, Foreground = DiskBars.Hex(bad ? "#92400E" : "#14532D"), TextWrapping = TextWrapping.Wrap,
            });
            foreach (var c in _clashes.GroupBy(x => x.Other).Select(g => g.OrderBy(x => x.FirstLocal).First()).OrderBy(x => x.FirstLocal))
                box.Children.Add(new TextBlock
                {
                    Text = $"•  {c.Other.Computer} ({c.Other.Job}, {c.Other.KindText.ToLowerInvariant()}): first on {c.FirstLocal:ddd d MMM} at {c.FirstLocal:HH:mm}",
                    Foreground = DiskBars.Hex("#92400E"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            if (bad)
            {
                box.Children.Add(new TextBlock
                {
                    Text = "Backups at the same time slow each other down and load the NAS. Pick a time after the other one finishes:",
                    Foreground = DiskBars.Hex("#92400E"), FontSize = 12, Margin = new Thickness(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap,
                });
                var buttons = new WrapPanel();
                foreach (var (editor, sched, minutes, label) in new[]
                         { (FullSchedule, full, Job.LastFullMinutes, "full"), (IncrSchedule, incr, Job.LastIncrementalMinutes, "incremental") })
                {
                    if (sched == null || !_clashes.Any(c => c.MineFull == (label == "full"))) continue;
                    string? free = ScheduleClash.SuggestTime(sched, minutes, _others, DateTime.Now);
                    if (free == null) continue;
                    var b = new Button { Content = $"Use {free} for the {label} backup", Tag = "", Margin = new Thickness(0, 0, 8, 0) };
                    b.Click += (_, _) => editor.SetTime(free);
                    buttons.Children.Add(b);
                }
                box.Children.Add(buttons);
            }
            ClashPanel.Children.Add(new Border
            {
                Background = DiskBars.Hex(bad ? "#FFFBEB" : "#F0FDF4"), BorderBrush = DiskBars.Hex(bad ? "#FDE68A" : "#BBF7D0"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10), Child = box,
            });
        }

        // Each other job, its times and usual length.
        OthersList.Children.Clear();
        foreach (var g in _others.GroupBy(r => (r.Computer, r.Job)))
        {
            bool clashing = _clashes.Any(c => c.Other.Computer == g.Key.Computer && c.Other.Job == g.Key.Job);
            var dot = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = DiskBars.Hex(clashing ? "#D97706" : "#22C55E"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 10, 0),
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{g.Key.Computer}  ·  {g.Key.Job}", FontWeight = FontWeights.SemiBold });
            foreach (var r in g)
                text.Children.Add(new TextBlock
                {
                    Text = $"{r.KindText}: {r.Schedule.Describe()}  ·  {r.LengthText}", Foreground = DiskBars.Hex("#475569"), FontSize = 12.5,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            OthersList.Children.Add(new Border { BorderBrush = DiskBars.Hex("#EEF2F6"), BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
        }
        OthersHint.Text = _others.Count == 0
            ? "No other PC has shared its backup times here yet. PCs with DiskImager 1.4 or later share them when their job is saved and after each backup."
            : "Times are shared by each PC with DiskImager 1.4 or later. A backup that hasn't run yet is counted as an hour long.";
    }

    // ------------------------------------------------------------------ review and save

    private void FillReview()
    {
        string Try(Func<string> f) { try { return f(); } catch (ArgumentException) { return "Needs fixing"; } }
        var rows = new List<(string, string)>
        {
            ("Disk", DiskList.SelectedIndex >= 0 && DiskList.SelectedIndex < _disks.Count ? _disks[DiskList.SelectedIndex].Title : Job.SourceDiskDescription),
            ("Job name", NameBox.Text.Trim()),
            ("Saved to", DestBox.Text.Trim().Length > 0 ? DestBox.Text.Trim() : "Not chosen yet"),
            ("Options", string.Join(" · ", new[]
            {
                (CompressionBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Replace(" (recommended)", "") + " compression",
                VerifyBox.IsChecked == true ? "verified when done" : "",
                TrackBox.IsChecked == true ? "faster incrementals" : "",
            }.Where(s => s.Length > 0))),
            ("Left out", _exclude.Count == 0 ? "Nothing: everything is backed up" : string.Join("\n", _exclude)),
            ("Encryption", Encrypting ? "AES-256 with a password" : "Off"),
        };
        if (_oneOff)
            rows.Add(("Backup type", OneOffIncr.IsChecked == true ? "Incremental" : "Full"));
        else
        {
            rows.Add(("Full backup", Try(() => FullSchedule.Read("Full backup").Describe())));
            rows.Add(("Incremental", Try(() => IncrSchedule.Read("Incremental backup").Describe())));
            rows.Add(("When done", (AfterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ""));
            rows.Add(("Email me", (NotifyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ""));
        }
        rows.Add(("Old backups", Try(() =>
        {
            var (keep, max) = CheckRetention();
            return (keep == 0 ? "Keep everything" : $"Keep the last {keep} full backup{(keep == 1 ? "" : "s")}")
                   + (max > 0 ? $" · a fresh full backup every {max} incrementals" : "");
        })));

        ReviewGrid.Children.Clear();
        ReviewGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            ReviewGrid.RowDefinitions.Add(new RowDefinition());
            var (label, value) = rows[i];
            var line = new Border { BorderBrush = DiskBars.Hex("#EEF2F6"), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            Grid.SetRow(line, i); Grid.SetColumnSpan(line, 2);
            ReviewGrid.Children.Add(line);
            var l = new TextBlock { Text = label, Foreground = DiskBars.Hex("#64748B"), Margin = new Thickness(0, 9, 0, 9) };
            var v = new TextBlock
            {
                Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 9),
                Foreground = DiskBars.Hex(value == "Needs fixing" ? "#B91C1C" : "#0F172A"),
                FontFamily = label == "Left out" && _exclude.Count > 0 ? new FontFamily("Cascadia Mono, Consolas") : FontFamily,
                FontSize = label == "Left out" && _exclude.Count > 0 ? 12 : FontSize,
            };
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            ReviewGrid.Children.Add(l);
            ReviewGrid.Children.Add(v);
        }
    }

    private void Save()
    {
        // Check every step; take the user to the first one that needs fixing.
        for (int i = 0; i < _steps.Count; i++)
        {
            try { _steps[i].Check(); }
            catch (ArgumentException ex)
            {
                Show(i);
                Warn(ex.Message);
                return;
            }
        }
        if (!_oneOff && _others != null)
        {
            UpdateClashes();
            if (_clashes.Count > 0 && AppMessage.Show(this,
                    $"This job backs up at the same time as {string.Join(", ", _clashes.Select(c => c.Other.Computer).Distinct())}.\n\n" +
                    "Both backups will be slower, and so will the NAS. You can pick another time on the Schedule step.\n\nSave it anyway?",
                    "Backups at the same time", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Show(_steps.FindIndex(s => s.Page == SchedulePage));
                return;
            }
        }
        string name = NameBox.Text.Trim();
        string dest = CheckLocation();
        string? encryption = ReadEncryption();
        var (keep, maxIncr) = CheckRetention();
        Schedule full = Job.Full, incr = Job.Incremental;
        if (!_oneOff)
        {
            full = FullSchedule.Read("Full backup");
            incr = IncrSchedule.Read("Incremental backup");
        }

        if (DiskList.SelectedIndex < _disks.Count)
        {
            var disk = _disks[DiskList.SelectedIndex];
            if (DiskEnumerator.DiskNumberOfPath(dest) == disk.Number &&
                AppMessage.Show(this, "The destination is on the same disk you're backing up. If that disk fails you lose the backup too.\n\nContinue anyway?",
                    "Same disk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Show(1);
                return;
            }
            Job.SourceDiskId = disk.Id;
            Job.SourceDiskDescription = disk.Title;
        }

        Job.Name = name;
        Job.Destination = dest;
        // A local destination never needs (or keeps) a network login.
        Job.NetUser = NetworkShare.IsNetworkPath(dest) && UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : null;
        if (Job.NetUser == null) Job.NetPasswordProtected = null;
        else if (PassBox.Password.Length > 0) Job.NetPasswordProtected = JobStore.Protect(PassBox.Password);
        Job.Compression = (CompressionChoice)Math.Max(0, CompressionBox.SelectedIndex);
        Job.Full = full;
        Job.Incremental = incr;
        Job.After = (PostAction)Math.Max(0, AfterBox.SelectedIndex);
        Job.WakeToRun = WakeBox.IsChecked == true;
        Job.VerifyAfter = VerifyBox.IsChecked == true;
        Job.ChangeTracking = TrackBox.IsChecked == true;
        Job.ExcludeFolders = _exclude.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Job.EncryptionPasswordProtected = encryption;
        if (!_oneOff) Job.Notify = (NotifyWhen)Math.Max(0, NotifyBox.SelectedIndex);
        Job.KeepFullSets = keep;
        Job.MaxIncrementals = maxIncr;
        DialogResult = true;
    }
}
