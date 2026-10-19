using System.Windows;
using DiskImager.Core;
using Microsoft.Win32;

namespace DiskImager;

public partial class JobEditorWindow : Window
{
    private readonly List<DiskInfo> _disks;
    private readonly bool _oneOff;

    public BackupJob Job { get; }
    public bool OneOffIncremental => OneOffIncr.IsChecked == true;

    public JobEditorWindow(List<DiskInfo> disks, BackupJob? job, bool oneOff, DiskInfo? preselect)
    {
        InitializeComponent();
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
        Loaded += (_, _) => DiskList.ScrollIntoView(DiskList.SelectedItem);

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
        NotifyHint.Text = AppSettings.Load().Email.Enabled ? "" : "Set up email on the Settings tab first.";
        ExcludeBox.Text = string.Join(Environment.NewLine, Job.ExcludeFolders);
        EncryptBox.IsChecked = Job.EncryptionPasswordProtected != null;
        if (Job.EncryptionPasswordProtected != null)
            EncPass1.ToolTip = EncPass2.ToolTip = "A password is saved. Leave both boxes empty to keep it.";
        EncryptPanel.IsEnabled = EncryptBox.IsChecked == true;
        KeepBox.Text = Job.KeepFullSets.ToString();
        MaxIncrBox.Text = Job.MaxIncrementals.ToString();
        if (!string.IsNullOrEmpty(Job.NetPasswordProtected))
            PassBox.ToolTip = "A password is saved. Leave empty to keep it.";
        InputBehaviors.AttachNumber(KeepBox, 3);
        InputBehaviors.AttachNumber(MaxIncrBox, 3);
        UpdateNetworkLogin();

        if (oneOff)
        {
            Title = Heading.Text = "Image a disk now";
            ScheduleGroup.Visibility = Visibility.Collapsed;
            OkButton.Content = "Start backup";
        }
        else
        {
            Title = Heading.Text = job == null ? "New scheduled backup" : $"Edit '{job.Name}'";
            OneOffGroup.Visibility = Visibility.Collapsed;
        }
    }

    private static FrameworkElement NotConnectedCard(string description) => new System.Windows.Controls.Border
    {
        Background = DiskBars.Hex("#F8FAFC"),
        BorderBrush = DiskBars.Hex("#CBD5E1"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
        Child = new System.Windows.Controls.StackPanel
        {
            Children =
            {
                new System.Windows.Controls.TextBlock { Text = description, FontWeight = FontWeights.SemiBold, Foreground = DiskBars.Hex("#475569") },
                new System.Windows.Controls.TextBlock
                {
                    Text = "Not connected right now. The job keeps this disk; backups run when it's plugged in.",
                    Foreground = DiskBars.Hex("#64748B"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                },
            },
        },
    };

    private void DestBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateNetworkLogin();

    /// <summary>The network login only applies to \\server\share destinations.</summary>
    private void UpdateNetworkLogin()
    {
        if (NetPanel == null) return;
        var v = NetworkShare.IsNetworkPath(DestBox.Text.Trim()) ? Visibility.Visible : Visibility.Collapsed;
        NetLabel.Visibility = NetPanel.Visibility = v;
    }

    private List<string> ExcludeLines() =>
        ExcludeBox.Text.Split('\n').Select(l => l.Trim().TrimEnd('\\')).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void EncryptBox_Click(object sender, RoutedEventArgs e) => EncryptPanel.IsEnabled = EncryptBox.IsChecked == true;

    /// <summary>The protected password to save, or null when encryption is off. Throws if the boxes don't agree.</summary>
    private string? ReadEncryption()
    {
        if (EncryptBox.IsChecked != true) return null;
        string a = EncPass1.Password, b = EncPass2.Password;
        if (a.Length == 0 && b.Length == 0 && Job.EncryptionPasswordProtected != null) return Job.EncryptionPasswordProtected;
        if (a.Length < 8) throw new ArgumentException("Choose a backup password of at least 8 characters.");
        if (a != b) throw new ArgumentException("The two backup passwords don't match.");
        return JobStore.Protect(a);
    }

    private void AddSuggested_Click(object sender, RoutedEventArgs e)
    {
        var lines = ExcludeLines();
        lines.AddRange(Exclusions.Suggested.Where(s => !lines.Contains(s, StringComparer.OrdinalIgnoreCase)));
        ExcludeBox.Text = string.Join(Environment.NewLine, lines);
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
            MessageBox.Show(this, "Destination OK: DiskImager can write there." + space, "Test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Can't write to that location:\n\n" + ex.Message, "Test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0) throw new ArgumentException("Give the job a name.");
            if (DiskList.SelectedIndex < 0) throw new ArgumentException("Choose the disk to back up.");
            string dest = DestBox.Text.Trim().TrimEnd('\\');
            if (dest.Length < 2) throw new ArgumentException("Choose where to save the images.");
            if (NetworkShare.IsNetworkPath(dest)) NetworkShare.ShareRoot(dest);
            else if (!(dest.Length >= 2 && dest[1] == ':')) throw new ArgumentException("Use a full path like E:\\Backups or \\\\NAS\\Backups.");
            string? encryption = ReadEncryption();
            var exclude = ExcludeLines();
            foreach (var x in exclude)
                if (Exclusions.Validate(x) is string why) throw new ArgumentException(why);
            if (!int.TryParse(KeepBox.Text, out int keep) || keep < 0) throw new ArgumentException("Enter how many full backups to keep (0 = keep everything).");
            if (!int.TryParse(MaxIncrBox.Text, out int maxIncr) || maxIncr < 0) throw new ArgumentException("Enter after how many incrementals to start a fresh full backup (0 = only on the full schedule).");

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
                    MessageBox.Show(this, "The destination is on the same disk you're backing up. If that disk fails you lose the backup too.\n\nContinue anyway?",
                        "Same disk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
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
            Job.ExcludeFolders = exclude;
            Job.EncryptionPasswordProtected = encryption;
            if (!_oneOff) Job.Notify = (NotifyWhen)Math.Max(0, NotifyBox.SelectedIndex);
            Job.KeepFullSets = keep;
            Job.MaxIncrementals = maxIncr;
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Check the details", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
