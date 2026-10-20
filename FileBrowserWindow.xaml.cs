using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskImager.Core;
using DiskImager.Core.Ntfs;
using Microsoft.Win32;

namespace DiskImager;

/// <summary>File-level restore: browse a backup's NTFS drives and copy files or folders out of it.</summary>
public partial class FileBrowserWindow : Window
{
    private readonly ImageReader _image;
    private readonly Logger _log;
    private List<(ImagePartition Partition, NtfsVolume Volume)> _volumes = new();
    private NtfsVolume? _vol;
    private ImagePartition? _part;
    private List<string> _excluded = new();
    private readonly List<NtfsEntry> _trail = new(); // root … current folder
    private CancellationTokenSource? _cts;

    private static readonly Brush FolderBrush = DiskBars.Hex("#D97706"), FileBrush = DiskBars.Hex("#64748B"),
        TextBrush = DiskBars.Hex("#0F172A"), MutedBrush = DiskBars.Hex("#94A3B8");

    public sealed record Row(NtfsEntry Entry, string Name, string Icon, Brush IconBrush, Brush TextBrush,
        string Modified, long Sort, long Size, string SizeText, string Note);

    /// <param name="image">An open (and, if encrypted, unlocked) image. The window disposes it when it closes.</param>
    public FileBrowserWindow(ImageReader image, Logger log)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        _image = image;
        _log = log;
        var h = image.Header;
        TitleText.Text = $"Browse the backup from {h.CreatedUtc.ToLocalTime():g}";
        SubtitleText.Text = $"{h.KindText} of {h.DiskModel} on {h.ComputerName} ({h.JobName}). Select files or folders and copy them to this PC. Nothing in the backup is changed.";
        Loaded += async (_, _) => await LoadVolumes();
        Closed += (_, _) => { _cts?.Cancel(); _image.Dispose(); };
    }

    private async Task LoadVolumes()
    {
        Busy("Reading the backup's drives…");
        try
        {
            _volumes = await Task.Run(() => FileRestore.OpenVolumes(_image));
            foreach (var (p, _) in _volumes) VolumeBox.Items.Add(FileRestore.Describe(p));
            if (_volumes.Count == 0) { Done("This backup has no NTFS drives that can be browsed (other file systems can still be restored as a whole disk)."); return; }
            int pick = _volumes.FindIndex(v => v.Partition.DriveLetter != null);
            VolumeBox.SelectedIndex = pick >= 0 ? pick : 0;
        }
        catch (Exception ex) { Done("Couldn't read the backup: " + ex.Message); }
    }

    private async void VolumeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeBox.SelectedIndex < 0) return;
        (_part, _vol) = _volumes[VolumeBox.SelectedIndex];
        _excluded = _image.Header.Exclusions.Where(x => x.PartitionOffset == _part.Offset).Select(x => x.Folder).ToList();
        var root = new NtfsEntry { Record = NtfsVolume.RootRecord, Name = _part.DriveLetter ?? "Drive", IsDirectory = true, Path = "\\" };
        FolderTree.Items.Clear();
        FolderTree.Items.Add(TreeItem(root, root.Name));
        _trail.Clear();
        _trail.Add(root);
        await ShowFolder();
    }

    private TreeViewItem TreeItem(NtfsEntry entry, string label)
    {
        var item = new TreeViewItem
        {
            Tag = entry,
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = FolderBrush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        item.Items.Add("…"); // placeholder until expanded
        item.Expanded += async (s, e) =>
        {
            if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not string) return;
            e.Handled = true;
            try
            {
                var dirs = await Task.Run(() => _vol!.ListDirectory(entry.Record, entry.Path).Where(d => d.IsDirectory && !d.IsLink).ToList());
                item.Items.Clear();
                foreach (var d in dirs) item.Items.Add(TreeItem(d, d.Name));
            }
            catch (Exception ex) { item.Items.Clear(); Done("Couldn't open that folder: " + ex.Message); }
        };
        return item;
    }

    private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item || item.Tag is not NtfsEntry) return;
        // Rebuild the trail from the tree item's ancestors.
        var chain = new List<NtfsEntry>();
        for (DependencyObject? d = item; d != null && d is not TreeView; d = ItemsControl.ItemsControlFromItemContainer(d))
            if (d is TreeViewItem t && t.Tag is NtfsEntry en) chain.Insert(0, en);
        _trail.Clear();
        _trail.AddRange(chain);
        await ShowFolder();
    }

    private async Task ShowFolder()
    {
        if (_vol == null || _trail.Count == 0) return;
        var folder = _trail[^1];
        PathText.Text = (_part?.DriveLetter ?? "") + (folder.Path == "\\" ? "\\" : folder.Path);
        UpButton.IsEnabled = _trail.Count > 1;
        Busy("Reading folder…");
        try
        {
            var items = await Task.Run(() => _vol.ListDirectory(folder.Record, folder.Path));
            FileGrid.ItemsSource = items.Select(MakeRow).ToList();
            bool excluded = FileRestore.IsExcluded(folder.Path, _excluded);
            Done($"{items.Count(i => i.IsDirectory):N0} folders, {items.Count(i => !i.IsDirectory):N0} files" +
                 (excluded ? " · this folder was excluded from the backup, so its files can't be restored" : ""));
        }
        catch (Exception ex)
        {
            FileGrid.ItemsSource = null;
            Done("Couldn't read this folder: " + ex.Message);
        }
    }

    private Row MakeRow(NtfsEntry e)
    {
        bool excluded = !e.IsDirectory && FileRestore.IsExcluded(e.Path, _excluded);
        string note = e.IsLink ? "Link (not copied)"
            : excluded ? "Not backed up"
            : e.Attributes.HasFlag(FileAttributes.Encrypted) ? "Encrypted by Windows"
            : e.Attributes.HasFlag(FileAttributes.Hidden) ? "Hidden" : "";
        return new Row(e, e.Name,
            e.IsLink ? "" : e.IsDirectory ? "" : "",
            e.IsDirectory ? FolderBrush : FileBrush,
            excluded || e.IsLink ? MutedBrush : TextBrush,
            e.ModifiedUtc > DateTime.MinValue ? e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "",
            e.ModifiedUtc.Ticks,
            e.IsDirectory ? -1 : e.Size,
            e.IsDirectory ? "" : Util.FormatBytes(e.Size),
            note);
    }

    private async void FileGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is Row { Entry: { IsDirectory: true, IsLink: false } dir })
        {
            _trail.Add(dir);
            await ShowFolder();
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_trail.Count <= 1) return;
        _trail.RemoveAt(_trail.Count - 1);
        await ShowFolder();
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CopyButton.IsEnabled = _cts == null && FileGrid.SelectedItems.Count > 0;

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var picked = FileGrid.SelectedItems.Cast<Row>().Select(r => r.Entry).ToList();
        if (picked.Count == 0 || _vol == null) return;
        var dlg = new OpenFolderDialog { Title = "Copy the selected items to which folder?" };
        if (dlg.ShowDialog(this) != true) return;
        string dest = dlg.FolderName;

        _cts = new CancellationTokenSource();
        CopyButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Busy("Copying…");
        var vol = _vol;
        var excluded = _excluded;
        var progress = new Progress<ProgressInfo>(p => StatusText.Text = $"{p.Stage} · {Util.FormatBytes(p.Done)} copied");
        try
        {
            var result = await Task.Run(() => FileRestore.Copy(vol, picked, dest, excluded, progress, _cts.Token, _log));
            Done($"Copied {result.Files:N0} files ({Util.FormatBytes(result.Bytes)}) to {dest}.");
            string msg = $"Copied {result.Files:N0} files in {result.Folders:N0} folders ({Util.FormatBytes(result.Bytes)}) to:\n{dest}";
            if (result.Problems.Count > 0)
                msg += $"\n\n{result.Problems.Count} item(s) couldn't be copied:\n• " + string.Join("\n• ", result.Problems.Take(8)) +
                       (result.Problems.Count > 8 ? "\n… (the full list is in the Activity log)" : "");
            MessageBox.Show(this, msg, "Copy finished", MessageBoxButton.OK, result.Problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { Done("Copy stopped. Files copied so far were kept."); }
        catch (Exception ex) { Done("Copy failed: " + ex.Message); }
        finally
        {
            _cts = null;
            CancelButton.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = FileGrid.SelectedItems.Count > 0;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Busy(string text)
    {
        StatusText.Text = text;
        Progress.Visibility = Visibility.Visible;
    }

    private void Done(string text)
    {
        StatusText.Text = text;
        Progress.Visibility = Visibility.Collapsed;
    }
}
