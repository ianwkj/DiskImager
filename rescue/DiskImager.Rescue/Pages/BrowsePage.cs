using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DiskImager.Core;
using DiskImager.Core.Ntfs;

namespace DiskImager.Rescue.Pages;

/// <summary>File-level restore: browse a backup's NTFS drives and copy files or folders to a USB drive.</summary>
public sealed class BrowsePage : UserControl
{
    private sealed record Row(NtfsEntry Entry, string Icon, string Name, string Modified, string Size, string Note, bool Muted);

    private readonly MainWindow _w;
    private readonly ComboBox _volumes = new() { MinWidth = 280 };
    private readonly TextBlock _path = Ui.Text("", 13.5, FontWeight.SemiBold);
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, MinHeight = 320, MaxHeight = 460, Background = Brushes.White, CornerRadius = new CornerRadius(10) };
    private readonly TextBlock _status = Ui.Hint("Opening the backup…");
    private readonly StackPanel _destinations = new() { Spacing = 8, IsVisible = false };
    private readonly Button _copy;
    private readonly Button _up;
    private ImageReader? _image;
    private List<(ImagePartition Partition, NtfsVolume Volume)> _vols = new();
    private NtfsVolume? _vol;
    private List<string> _excluded = new();
    private readonly List<NtfsEntry> _trail = new();

    public BrowsePage(MainWindow w)
    {
        _w = w;
        var h = w.State.Image!.Header;
        _copy = Ui.Button("Copy selected to…", ShowDestinations, "primary", enabled: false);
        _up = Ui.Button("⬆ Up", async () => { if (_trail.Count > 1) { _trail.RemoveAt(_trail.Count - 1); await ShowFolder(); } });
        _list.ItemTemplate = new FuncDataTemplate<Row>((r, _) => r == null ? new Border() : Ui.Columns("28,*,150,90,150",
            Ui.Text(r.Icon, 15),
            new TextBlock { Text = r.Name, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Hex(r.Muted ? "#94A3B8" : "#0F172A"), VerticalAlignment = VerticalAlignment.Center },
            Ui.Hint(r.Modified), Ui.Hint(r.Size), Ui.Hint(r.Note)));
        _list.SelectionChanged += (_, _) => _copy.IsEnabled = _list.SelectedItems?.Count > 0;
        _list.DoubleTapped += async (_, _) =>
        {
            if (_list.SelectedItem is Row { Entry: { IsDirectory: true, IsLink: false } dir })
            {
                _trail.Add(dir);
                await ShowFolder();
            }
        };
        _volumes.SelectionChanged += async (_, _) => await PickVolume();

        Content = Ui.Stack(0,
            Ui.Title("Browse and copy files"),
            Ui.Subtitle($"{h.ComputerName} · {h.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} · {h.KindText}. Double-click a folder to open it; select files or folders, then copy them to a USB drive. The backup isn't changed."),
            Ui.Row(10, Ui.Text("Drive", 14), _volumes, _up),
            new Border { Height = 10 },
            Ui.Card(Ui.Stack(8, _path, _list)),
            _status,
            _destinations,
            Ui.ButtonRow(Ui.Button("Back", () => { Dispose(); w.Show(new FindBackupPage(w), 1); }), _copy));
        AttachedToVisualTree += async (_, _) => { await Open(); _opened.TrySetResult(); };
        DetachedFromVisualTree += (_, _) => Dispose();
    }

    // Hooks for the demo self-test.
    private readonly TaskCompletionSource _opened = new();
    internal Task Opened => _opened.Task;
    internal int ItemCount => (_list.ItemsSource as System.Collections.ICollection)?.Count ?? 0;

    private void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }

    private async Task Open()
    {
        try
        {
            var s = _w.State;
            _image = new ImageReader(s.Image!.Path);
            var missing = _image.MissingChainFiles();
            if (missing.Count > 0) throw new InvalidOperationException(missing[0]);
            if (_image.IsEncrypted) _image.Unlock(s.Password);
            var image = _image;
            _vols = await Task.Run(() => FileRestore.OpenVolumes(image));
            if (_vols.Count == 0) { _status.Text = "This backup has no NTFS drives that can be browsed."; return; }
            _volumes.ItemsSource = _vols.Select(v => FileRestore.Describe(v.Partition)).ToList();
            int pick = _vols.FindIndex(v => v.Partition.DriveLetter != null);
            _volumes.SelectedIndex = pick >= 0 ? pick : 0;
        }
        catch (Exception ex) { _status.Text = "Couldn't open the backup: " + ex.Message; }
    }

    private async Task PickVolume()
    {
        if (_volumes.SelectedIndex < 0) return;
        var (p, v) = _vols[_volumes.SelectedIndex];
        _vol = v;
        _excluded = _image!.Header.Exclusions.Where(x => x.PartitionOffset == p.Offset).Select(x => x.Folder).ToList();
        _trail.Clear();
        _trail.Add(new NtfsEntry { Record = NtfsVolume.RootRecord, Name = p.DriveLetter ?? "Drive", IsDirectory = true, Path = "\\" });
        await ShowFolder();
    }

    private async Task ShowFolder()
    {
        if (_vol == null) return;
        var folder = _trail[^1];
        _path.Text = "📁  " + (_vols[_volumes.SelectedIndex].Partition.DriveLetter ?? "") + folder.Path;
        _up.IsEnabled = _trail.Count > 1;
        _status.Text = "Reading folder…";
        try
        {
            var vol = _vol;
            var items = await Task.Run(() => vol.ListDirectory(folder.Record, folder.Path));
            _list.ItemsSource = items.Select(e =>
            {
                bool excluded = !e.IsDirectory && FileRestore.IsExcluded(e.Path, _excluded);
                return new Row(e, e.IsLink ? "↪" : e.IsDirectory ? "📁" : "📄", e.Name,
                    e.ModifiedUtc > DateTime.MinValue ? e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "",
                    e.IsDirectory ? "" : Util.FormatBytes(e.Size),
                    e.IsLink ? "Link" : excluded ? "Not backed up" : e.Attributes.HasFlag(FileAttributes.Encrypted) ? "Encrypted by Windows" : "",
                    e.IsLink || excluded);
            }).ToList();
            _status.Text = $"{items.Count(i => i.IsDirectory):N0} folders, {items.Count(i => !i.IsDirectory):N0} files";
        }
        catch (Exception ex) { _status.Text = "Couldn't read this folder: " + ex.Message; }
    }

    private async void ShowDestinations()
    {
        _destinations.Children.Clear();
        _destinations.IsVisible = true;
        _destinations.Children.Add(Ui.Label("Copy to which drive?"));
        var drives = await Task.Run(() => _w.Platform.WritableDrives(_w.Log));
        if (drives.Count == 0) _destinations.Children.Add(Ui.Banner("No USB drive to copy to. Plug one in, then click Copy again."));
        foreach (var (name, path) in drives)
        {
            var target = path;
            _destinations.Children.Add(Ui.Card(Ui.Row(10, Ui.Text("🔌", 18), Ui.Text(name, 14, FontWeight.SemiBold)), async () => await Copy(target)));
        }
    }

    private async Task Copy(string drive)
    {
        if (_vol == null || _image == null) return;
        var picked = _list.SelectedItems?.Cast<Row>().Select(r => r.Entry).ToList() ?? new();
        if (picked.Count == 0) return;
        var h = _image.Header;
        string dest = Path.Combine(drive, $"Restored from {h.ComputerName} {h.CreatedUtc.ToLocalTime():yyyy-MM-dd HHmm}");
        _destinations.IsVisible = false;
        _copy.IsEnabled = false;
        _w.Busy = true;
        var progress = new Progress<ProgressInfo>(p => _status.Text = $"{p.Stage} · {Util.FormatBytes(p.Done)} copied");
        try
        {
            var vol = _vol;
            var excluded = _excluded;
            var result = await Task.Run(() => FileRestore.Copy(vol, picked, dest, excluded, progress, CancellationToken.None, _w.Log));
            string msg = $"Copied {result.Files:N0} files in {result.Folders:N0} folders ({Util.FormatBytes(result.Bytes)}) to {dest}.";
            if (result.Problems.Count > 0) msg += $"\n{result.Problems.Count} item(s) couldn't be copied:\n• " + string.Join("\n• ", result.Problems.Take(6));
            _status.Text = "";
            _destinations.Children.Clear();
            _destinations.IsVisible = true;
            _destinations.Children.Add(Ui.Banner(msg, success: result.Problems.Count == 0));
        }
        catch (Exception ex) { _status.Text = "Copy failed: " + ex.Message; }
        finally
        {
            _w.Busy = false;
            _copy.IsEnabled = _list.SelectedItems?.Count > 0;
        }
    }
}
