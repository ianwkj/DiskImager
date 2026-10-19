using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskImager.Core;
using Microsoft.Win32;

namespace DiskImager;

public partial class MainWindow : Window
{
    private readonly Logger _log = new();
    private CancellationTokenSource? _cts;
    private volatile bool _manualExtendNeeded;
    private readonly System.Windows.Threading.DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _showingBackground;
    private List<DiskInfo> _disks = new();

    private sealed record JobRow(BackupJob Job, string Name, string Source, string Destination, string Full,
        string Incremental, string Keep, string After, string LastRun);

    private sealed record ImageRow(ImageEntry Entry, string Created, string Kind, string Job, string Computer,
        string Disk, string Size, string Status, string File);

    public MainWindow()
    {
        InitializeComponent();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v != null) VersionText.Text = AboutVersion.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        _log.LineWritten += line =>
        {
            if (line.Contains("extend filesystem", StringComparison.OrdinalIgnoreCase)) _manualExtendNeeded = true;
        };
        _log.LineWritten += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
        InputBehaviors.AttachNumber(SmtpPortBox, 5);
        _backgroundTimer.Tick += async (_, _) => await ShowBackgroundRuns();
        _backgroundTimer.Start();
        Loaded += async (_, _) =>
        {
            LoadEmailSettings();
            _ = Task.Run(() =>
            {
                try { TaskSchedulerService.RefreshInstalledCopy(_log); }
                catch (Exception ex) { _log.Warn("Could not update the scheduled-task copy of DiskImager: " + ex.Message); }
            });
            await RefreshDisks();
            RefreshJobs();
        };
    }

    // ---------------------------------------------------------------- Disks

    private async Task RefreshDisks()
    {
        StageText.Text = "Reading disks…";
        try
        {
            _disks = await Task.Run(DiskEnumerator.GetDisks);
        }
        catch (Exception ex)
        {
            _log.Error("Could not list disks: " + ex.Message);
        }
        int? restoreSel = ((RestoreDiskList.SelectedItem as FrameworkElement)?.Tag as DiskInfo)?.Number;
        DiskList.Items.Clear();
        RestoreDiskList.Items.Clear();
        foreach (var d in _disks)
        {
            DiskList.Items.Add(DiskBars.Card(d));
            RestoreDiskList.Items.Add(DiskBars.Card(d, d.IsSystem ? "CAN'T RESTORE HERE WHILE WINDOWS IS RUNNING" : null));
        }
        if (DiskList.Items.Count > 0) DiskList.SelectedIndex = 0;
        int idx = _disks.FindIndex(d => d.Number == restoreSel);
        if (idx >= 0) RestoreDiskList.SelectedIndex = idx;
        StageText.Text = "Ready";
    }

    private DiskInfo? SelectedDisk() => (DiskList.SelectedItem as FrameworkElement)?.Tag as DiskInfo;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDisks();

    private async void ImageDisk_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk();
        if (disk == null) { Info("Select a disk first."); return; }

        var editor = new JobEditorWindow(_disks, null, oneOff: true, preselect: disk) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var job = editor.Job;
        bool incremental = editor.OneOffIncremental;

        await RunOperation($"Backing up Disk {disk.Number}", (p, ct) =>
        {
            var r = JobRunner.RunJob(job, incremental, p, ct, _log);
            return $"Backup complete.\n\n{r.ImagePath}\n{Util.FormatBytes(r.BytesWritten)} written in {Util.FormatDuration(r.Duration)}." + (r.Verified ? "\nImage verified: every block checked." : "");
        });
    }

    private async void CloneDisk_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedDisk();
        if (source == null) { Info("Select the disk you want to copy first."); return; }

        var targets = _disks.Where(d => d.Number != source.Number).ToList();
        var dlg = new LayoutWindow($"Clone Disk {source.Number} ({source.Model})",
            "Every partition on the source disk is copied to the drive you choose. The target can be smaller or larger: partitions are resized to fit.",
            SourceDescription.FromDisk(source), targets, null, source.Number) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var target = dlg.Target!;
        var plan = dlg.Plan!;
        bool newIds = dlg.NewIds;
        string? staging = dlg.StagingFolder;

        await RunOperation($"Cloning Disk {source.Number} to Disk {target.Number}", (p, ct) =>
        {
            using var src = LiveDiskSource.Open(source, useVss: true, _log, ct);
            DiskCopy.Execute(src, plan, target, newIds, staging, "Cloning", p, ct, _log);
            return $"Disk {source.Number} has been cloned to Disk {target.Number}." + ExtendNote();
        });
        await RefreshDisks();
    }

    // ---------------------------------------------------------------- Jobs

    private void RefreshJobs()
    {
        JobsGrid.ItemsSource = JobStore.Load().Select(j => new JobRow(j, j.Name, j.SourceDiskDescription, j.Destination,
            j.Full.Describe(), j.Incremental.Describe(),
            j.KeepFullSets == 0 ? "All" : $"{j.KeepFullSets} sets",
            j.After == PostAction.None ? "–" : j.After.ToString(),
            j.LastRunUtc == null ? "Never" : $"{j.LastRunUtc.Value.ToLocalTime():g}  {j.LastResult}")).ToList();
    }

    private BackupJob? SelectedJob() => (JobsGrid.SelectedItem as JobRow)?.Job;

    private void NewJob_Click(object sender, RoutedEventArgs e)
    {
        var editor = new JobEditorWindow(_disks, null, oneOff: false, preselect: SelectedDisk()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        SaveJob(editor.Job);
    }

    private void EditJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob();
        if (job == null) return;
        var editor = new JobEditorWindow(_disks, job.Clone(), oneOff: false, preselect: null) { Owner = this };
        if (editor.ShowDialog() != true) return;
        SaveJob(editor.Job);
    }

    private void SaveJob(BackupJob job)
    {
        var jobs = JobStore.Load();
        if (jobs.Any(j => j.Id != job.Id && string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Error("Another job already has that name. Job names must be unique because they're used in image file names.");
            return;
        }
        try
        {
            TaskSchedulerService.Register(job, _log);
        }
        catch (Exception ex)
        {
            Error("The job was saved, but Windows Task Scheduler could not schedule it:\n\n" + ex.Message);
        }
        // A saved schedule starts fresh: earlier times are never reported as missed.
        job.ScheduleSinceUtc = DateTime.UtcNow;
        job.MissedNotifiedForUtc = null;
        jobs.RemoveAll(j => j.Id == job.Id);
        jobs.Add(job);
        JobStore.Save(jobs);
        SyncMissedCheck();
        RefreshJobs();
    }

    private void SyncMissedCheck()
    {
        try { TaskSchedulerService.SyncMissedCheck(_log); }
        catch (Exception ex) { _log.Warn("Could not set up the missed-backup check: " + ex.Message); }
    }

    private void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob();
        if (job == null) return;
        if (MessageBox.Show(this, $"Delete the job '{job.Name}' and its schedule?\n\nExisting image files are kept.",
                "Delete job", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        TaskSchedulerService.Unregister(job);
        var jobs = JobStore.Load();
        jobs.RemoveAll(j => j.Id == job.Id);
        JobStore.Save(jobs);
        SyncMissedCheck();
        RefreshJobs();
    }

    private async void RunFull_Click(object sender, RoutedEventArgs e) => await RunJobNow(false);
    private async void RunIncr_Click(object sender, RoutedEventArgs e) => await RunJobNow(true);

    private async Task RunJobNow(bool incremental)
    {
        var job = SelectedJob();
        if (job == null) { Info("Select a job first."); return; }
        await RunOperation($"Running '{job.Name}' ({(incremental ? "incremental" : "full")})", (p, ct) =>
        {
            var r = JobRunner.RunJobAndReport(job, incremental, p, ct, _log);
            return $"Backup complete.\n\n{r.ImagePath}\n{Util.FormatBytes(r.BytesWritten)} written in {Util.FormatDuration(r.Duration)}." + (r.Verified ? "\nImage verified: every block checked." : "");
        });
        RefreshJobs();
    }

    // ---------------------------------------------------------------- Restore

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) => Links.OnRequestNavigate(sender, e);

    /// <summary>The network login only applies to \\server\share locations.</summary>
    private void LocationBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RestoreNetPanel.Visibility = NetworkShare.IsNetworkPath(LocationBox.Text.Trim()) ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseLocation_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder that contains your .dimg images" };
        if (dlg.ShowDialog(this) == true) LocationBox.Text = dlg.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        string location = LocationBox.Text.Trim();
        if (location.Length == 0) { Info("Enter or browse to the folder where your images are stored."); return; }
        bool net = NetworkShare.IsNetworkPath(location);
        string user = net ? RestoreUserBox.Text.Trim() : "", pass = net ? RestorePassBox.Password : "";
        StageText.Text = "Looking for images…";
        try
        {
            var chains = await Task.Run(() =>
            {
                NetworkShare.Connect(location, user, pass, _log);
                if (!Directory.Exists(location)) throw new DirectoryNotFoundException($"Can't find or open {location}.");
                return ImageCatalog.Chains(ImageCatalog.Scan(location, depth: 2));
            });
            var rows = new List<ImageRow>();
            foreach (var c in chains)
            {
                for (int i = 0; i < c.Images.Count; i++)
                {
                    var img = c.Images[i];
                    var h = img.Header;
                    bool ok = Enumerable.Range(0, h.ChainOrdinal + 1).All(k => k < c.Images.Count && c.Images[k].Header.ChainOrdinal == k);
                    rows.Add(new ImageRow(img, h.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), h.KindText, h.JobName,
                        h.ComputerName, $"{h.DiskModel} ({Util.FormatBytes(h.DiskSize)})", Util.FormatBytes(img.FileSize),
                        img.Damaged ? "Damaged" : ok ? "Ready" : "Missing files", System.IO.Path.GetFileName(img.Path)));
                }
            }
            ImagesGrid.ItemsSource = rows.OrderByDescending(r => r.Entry.Header.CreatedUtc).ToList();
            StageText.Text = rows.Count == 0 ? "No images found in that location." : $"Found {rows.Count} images.";
        }
        catch (Exception ex)
        {
            StageText.Text = "Ready";
            Error(ex.Message);
        }
    }

    private ImageEntry? SelectedImage() => (ImagesGrid.SelectedItem as ImageRow)?.Entry;

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var img = SelectedImage();
        if (img == null) { Info("Find images first, then select the backup point to restore."); return; }
        var h = img.Header;
        int? imageDisk = DiskEnumerator.DiskNumberOfPath(img.Path);
        var targets = _disks.Where(d => d.Number != imageDisk).ToList();
        var chosen = (RestoreDiskList.SelectedItem as FrameworkElement)?.Tag as DiskInfo;
        if (chosen != null && chosen.Number == imageDisk) { Error("That drive holds the image you're restoring from. Pick a different drive."); return; }
        if (chosen?.IsSystem == true) { Error("Windows is running from that drive, so it can't be overwritten. Pick a different drive."); return; }

        SourceDescription source;
        try
        {
            using var r = new ImageReader(img.Path);
            var missing = r.MissingChainFiles();
            if (missing.Count > 0) { Error(missing[0]); return; }
            source = SourceDescription.FromImage(r);
        }
        catch (Exception ex) { Error(ex.Message); return; }

        var dlg = new LayoutWindow($"Restore {h.KindText} from {h.CreatedUtc.ToLocalTime():g}",
            $"Image of {h.DiskModel} ({Util.FormatBytes(h.DiskSize)}) from {h.ComputerName}. Every partition is written to the drive you choose; partitions can be resized to fit a smaller or larger drive.",
            source, targets, chosen, null) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var target = dlg.Target!;
        var plan = dlg.Plan!;
        bool newIds = dlg.NewIds;
        string? staging = dlg.StagingFolder;

        await RunOperation($"Restoring to Disk {target.Number}", (p, ct) =>
        {
            using var reader = new ImageReader(img.Path);
            DiskCopy.Execute(reader, plan, target, newIds, staging, "Restoring", p, ct, _log);
            return $"Restore complete. Disk {target.Number} now holds the backup from {h.CreatedUtc.ToLocalTime():g}." + ExtendNote();
        });
        await RefreshDisks();
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var img = SelectedImage();
        if (img == null) { Info("Select an image to verify."); return; }
        await RunOperation("Verifying image", (p, ct) =>
        {
            using var reader = new ImageReader(img.Path);
            int bad = reader.Verify(p, ct, _log);
            if (bad > 0) throw new InvalidDataException($"{bad} damaged blocks found. Don't rely on this image; see the Log tab.");
            _log.Info($"Verified {System.IO.Path.GetFileName(img.Path)}: all blocks OK.");
            return "The image is intact: every block matches its checksum.";
        });
    }

    private void ImagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = SelectedImage() != null;
        VerifyButton.IsEnabled = RestoreButton.IsEnabled = selected;
        VerifyButton.ToolTip = selected ? "Check every block of this backup against its checksum" : "Select a backup first";
        RestoreButton.ToolTip = selected ? null : "Select a backup first";
    }

    private void ImageRow_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row) row.IsSelected = true;
    }

    private void CopyImagePath_Click(object sender, RoutedEventArgs e)
    {
        var img = SelectedImage();
        if (img == null) return;
        try
        {
            Clipboard.SetText(img.Path);
            StageText.Text = "Copied the file path to the clipboard";
            DetailText.Text = img.Path;
        }
        catch (Exception ex) { Error("Couldn't copy to the clipboard: " + ex.Message); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Logger.LogFolder);
        Process.Start("explorer.exe", Logger.LogFolder);
    }

    // ---------------------------------------------------------------- Settings

    private void LoadEmailSettings()
    {
        var s = AppSettings.Load().Email;
        EmailEnabledBox.IsChecked = s.Enabled;
        SmtpHostBox.Text = s.Host;
        SmtpPortBox.Text = s.Port.ToString();
        SmtpSecurityBox.SelectedIndex = (int)s.Security;
        SmtpUserBox.Text = s.User ?? "";
        SmtpFromBox.Text = s.From;
        SmtpToBox.Text = s.To;
        if (!string.IsNullOrEmpty(s.PasswordProtected)) SmtpPassBox.ToolTip = "A password is saved. Leave empty to keep it.";
    }

    /// <summary>Settings from the form; an empty password box keeps the saved password.</summary>
    private EmailSettings ReadEmailForm()
    {
        if (!int.TryParse(SmtpPortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
            throw new ArgumentException("The port must be a number such as 587.");
        string host = SmtpHostBox.Text.Trim();
        string to = SmtpToBox.Text.Trim();
        if (EmailEnabledBox.IsChecked == true && (host.Length == 0 || to.Length == 0))
            throw new ArgumentException("Enter the SMTP server and at least one address to send to.");
        string user = SmtpUserBox.Text.Trim();
        return new EmailSettings
        {
            Enabled = EmailEnabledBox.IsChecked == true,
            Host = host,
            Port = port,
            Security = (EmailSecurity)Math.Max(0, SmtpSecurityBox.SelectedIndex),
            User = user.Length > 0 ? user : null,
            PasswordProtected = user.Length == 0 ? null
                : SmtpPassBox.Password.Length > 0 ? JobStore.Protect(SmtpPassBox.Password)
                : AppSettings.Load().Email.PasswordProtected,
            From = SmtpFromBox.Text.Trim(),
            To = to,
        };
    }

    private void SaveEmail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load();
            settings.Email = ReadEmailForm();
            settings.Save();
            SmtpPassBox.Clear();
            if (settings.Email.PasswordProtected != null) SmtpPassBox.ToolTip = "A password is saved. Leave empty to keep it.";
            EmailStatus.Foreground = DiskBars.Hex("#14532D");
            EmailStatus.Text = "Saved.";
        }
        catch (ArgumentException ex) { Error(ex.Message); }
    }

    private async void TestEmail_Click(object sender, RoutedEventArgs e)
    {
        EmailSettings s;
        try { s = ReadEmailForm(); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        EmailStatus.Foreground = DiskBars.Hex("#475569");
        EmailStatus.Text = "Sending…";
        try
        {
            await Task.Run(() => Notifier.Send(s, Notifier.TestMessage()));
            EmailStatus.Foreground = DiskBars.Hex("#14532D");
            EmailStatus.Text = $"Test email sent to {s.To}. Remember to click Save.";
            _log.Info($"Test email sent to {s.To}.");
        }
        catch (Exception ex)
        {
            EmailStatus.Foreground = DiskBars.Hex("#991B1B");
            EmailStatus.Text = "Failed: " + ex.Message;
            _log.Warn("Test email failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- Scheduled runs happening in the background

    /// <summary>Shows scheduled backups (run by Windows as separate processes) in the status bar.</summary>
    private async Task ShowBackgroundRuns()
    {
        if (_cts != null) return; // this window's own operation owns the status bar
        var runs = await Task.Run(() => BackgroundStatus.ReadAll());
        if (_cts != null) return;

        if (runs.Count == 0)
        {
            if (!_showingBackground) return;
            _showingBackground = false;
            StageText.Text = "Ready";
            DetailText.Text = "The scheduled backup has finished. See the Scheduled backups page for the result.";
            Progress.Value = 0;
            StatusDot.Background = DiskBars.Hex("#22C55E");
            RefreshJobs();
            return;
        }

        _showingBackground = true;
        var active = runs.FirstOrDefault(r => r.Phase == RunPhase.Running) ?? runs[0];
        int waiting = runs.Count(r => r.Waiting && r != active);
        StatusDot.Background = DiskBars.Hex(active.Phase == RunPhase.Running ? "#3B82F6" : "#F59E0B");
        StageText.Text = active.Phase switch
        {
            RunPhase.Waiting => $"Scheduled backup '{active.JobName}' is waiting for another backup to finish",
            RunPhase.Countdown => $"Scheduled backup '{active.JobName}' is about to start",
            RunPhase.Postponed => $"Scheduled backup '{active.JobName}' was postponed until {active.PostponedUntilUtc?.ToLocalTime():HH:mm}",
            _ => $"Scheduled backup '{active.JobName}' is running · {active.Stage}",
        };
        double pct = active.Total > 0 ? Math.Min(100, 100.0 * active.Done / active.Total) : 0;
        Progress.Value = pct;
        string detail = active.Total > 0
            ? $"{pct:0}%  ·  {Util.FormatBytes(active.Done)} of {Util.FormatBytes(active.Total)}  ·  {Util.FormatBytes(active.BytesPerSecond)}/s"
            : $"Started {active.StartedUtc.ToLocalTime():HH:mm}";
        if (waiting > 0) detail += $"  ·  {waiting} more queued";
        DetailText.Text = detail + "  ·  runs in the background, you can close this window";
    }

    // ---------------------------------------------------------------- Operation runner

    private async Task RunOperation(string title, Func<IProgress<ProgressInfo>, CancellationToken, string> work)
    {
        if (_cts != null) { Info("Another operation is already running."); return; }
        _cts = new CancellationTokenSource();
        _manualExtendNeeded = false;
        _showingBackground = false;
        var token = _cts.Token;
        var after = (PostAction)AfterCombo.SelectedIndex;
        SetBusy(true);
        StageText.Text = title;
        DetailText.Text = "Preparing…";
        Progress.Value = 0;
        var progress = new Progress<ProgressInfo>(ShowProgress);

        string? message = null, error = null;
        try
        {
            message = await Task.Run(() =>
            {
                using var opLock = OperationLock.TryAcquire(TimeSpan.Zero)
                    ?? throw new InvalidOperationException("Another DiskImager operation (possibly a scheduled backup) is already running.");
                // Released when the work ends, before any "when finished" sleep/shutdown runs.
                using var awake = new KeepAwake($"DiskImager: {title}", _log);
                return work(progress, token);
            });
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled.";
            _log.Warn(title + ": cancelled.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error($"{title} failed: {ex}");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }

        StageText.Text = error == null ? "Finished: " + title : "Failed: " + title;
        DetailText.Text = error ?? "";
        if (error == null) Progress.Value = 100;
        StatusDot.Background = DiskBars.Hex(error == null ? "#22C55E" : error == "Cancelled." ? "#F59E0B" : "#EF4444");

        if (after != PostAction.None && error != "Cancelled.")
        {
            PowerActions.Execute(after, _log);
            return;
        }
        if (error == null) Info(message!);
        else Error(error);
    }

    private void ShowProgress(ProgressInfo p)
    {
        StageText.Text = p.Stage;
        double pct = p.Total > 0 ? Math.Min(100, 100.0 * p.Done / p.Total) : 0;
        Progress.Value = pct;
        string eta = p.BytesPerSecond > 0 && p.Total > p.Done
            ? "  ·  " + Util.FormatDuration(TimeSpan.FromSeconds((p.Total - p.Done) / p.BytesPerSecond)) + " left"
            : "";
        DetailText.Text = $"{pct:0}%  ·  {Util.FormatBytes(p.Done)} of {Util.FormatBytes(p.Total)}  ·  {Util.FormatBytes(p.BytesPerSecond)}/s{eta}";
    }

    private void SetBusy(bool busy)
    {
        DisksPanel.IsEnabled = JobsPanel.IsEnabled = RestorePanel.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        if (busy) StatusDot.Background = DiskBars.Hex("#3B82F6");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        if (MessageBox.Show(this, "Stop the current operation?\n\nA cancelled restore or clone leaves the target disk unusable until you run it again.",
                "Cancel", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _cts?.Cancel();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cts != null && MessageBox.Show(this, "An operation is still running. Close anyway and cancel it?", "DiskImager",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private string ExtendNote() => _manualExtendNeeded
        ? "\n\nOne or more file systems couldn't be extended automatically. See the Log tab for how to finish it."
        : "";

    private void Info(string msg) => MessageBox.Show(this, msg, "DiskImager", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Error(string msg) => MessageBox.Show(this, msg, "DiskImager", MessageBoxButton.OK, MessageBoxImage.Error);
}
