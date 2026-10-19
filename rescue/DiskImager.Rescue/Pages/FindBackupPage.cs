using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiskImager.Core;
using DiskImager.Rescue.Platform;

namespace DiskImager.Rescue.Pages;

/// <summary>Find the backup: drives are searched automatically, a NAS is connected once; then pick a backup point.</summary>
public sealed class FindBackupPage : UserControl
{
    private readonly MainWindow _w;
    private readonly StackPanel _locations = new() { Spacing = 8 };
    private readonly StackPanel _points = new() { Spacing = 8 };
    private readonly TextBlock _status = Ui.Hint("Looking for backups on the attached drives…");
    private readonly TextBox _share = new() { Watermark = @"\\NAS\Backups  or  \\192.168.1.10\Backups" };
    private readonly TextBox _user = new() { Watermark = @"User name, e.g. NAS\backupuser" };
    private readonly TextBox _pass = new() { Watermark = "Password", PasswordChar = '•' };
    private readonly Button _connect;
    private readonly Button _next;
    private readonly TextBlock _error = new() { Foreground = Ui.Hex("#B91C1C"), TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _passwordPanel = new() { Spacing = 8, IsVisible = false, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBox _imagePassword = new() { PasswordChar = '•', Watermark = "Backup password", Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private List<ImageEntry> _images = new();
    private ImageEntry? _picked;
    private string? _selectedLocation;

    public FindBackupPage(MainWindow w)
    {
        _w = w;
        _connect = Ui.Button("Connect", async () => await ConnectShare(), "primary");
        _next = Ui.Button("Next", async () => await Next(), "primary", enabled: false);
        _passwordPanel.Children.Add(Ui.Text("🔒  This backup is encrypted. Enter its password:", 14, FontWeight.SemiBold));
        _passwordPanel.Children.Add(_imagePassword);

        var nas = Ui.Card(Ui.Stack(8,
            Ui.Row(8, Ui.Text("🗄", 18), Ui.Text("NAS or network share", 14, FontWeight.SemiBold)),
            _share, _user, _pass,
            _connect));

        var left = Ui.Stack(0, Ui.Label("Where"), _locations, new Border { Height = 10 }, nas);
        var right = Ui.Stack(0, Ui.Label("Backup points · newest first"), _status, _points, _passwordPanel, _error);
        var cols = Ui.Columns("320,24,*", left, new Border(), right);

        Content = Ui.Stack(0,
            Ui.Title("Find the backup"),
            Ui.Subtitle("Backups on drives plugged into this PC are found automatically. For a NAS, enter its address and login."),
            cols,
            Ui.ButtonRow(Ui.Button("Back", () => w.GoHome()), _next));

        AttachedToVisualTree += async (_, _) => { await Search(); _loaded.TrySetResult(); };
    }

    // Hooks for the demo self-test.
    private readonly TaskCompletionSource _loaded = new();
    internal Task LoadTask => _loaded.Task;
    internal void PickNewest(string password)
    {
        var first = _images.FirstOrDefault();
        if (first != null) Pick(first);
        _imagePassword.Text = password;
    }
    internal Task NextAsync() => Next();

    private async Task Search()
    {
        _status.Text = "Looking for backups on the attached drives…";
        List<ImageLocation> found;
        try { found = await Task.Run(() => _w.Platform.FindImageLocations(_w.Log)); }
        catch (Exception ex) { _status.Text = "Couldn't search the drives: " + ex.Message; return; }
        _locations.Children.Clear();
        if (found.Count == 0)
            _locations.Children.Add(Ui.Hint("No backups on the attached drives. Plug in the backup drive, or connect to the NAS below."));
        foreach (var loc in found) AddLocation(loc);
        if (found.Count > 0) await ShowImages(found[0].Path);
        else _status.Text = "Choose where the backups are.";
    }

    private void AddLocation(ImageLocation loc)
    {
        var card = Ui.Card(Ui.Row(10,
            Ui.Text(loc.Kind == "nas" ? "🗄" : "💾", 18),
            Ui.Stack(2, Ui.Text(loc.Name, 13.5, FontWeight.SemiBold), Ui.Hint($"{loc.Images} backup file{(loc.Images == 1 ? "" : "s")}"))),
            async () => await ShowImages(loc.Path), selected: loc.Path == _selectedLocation);
        card.Tag = loc.Path;
        _locations.Children.Add(card);
    }

    private void MarkLocation()
    {
        foreach (var c in _locations.Children.OfType<Border>())
        {
            c.Classes.Remove("selected");
            if (c.Tag as string == _selectedLocation) c.Classes.Add("selected");
        }
    }

    private async Task ShowImages(string path)
    {
        _selectedLocation = path;
        MarkLocation();
        _status.Text = "Reading backups…";
        _points.Children.Clear();
        _picked = null;
        _next.IsEnabled = false;
        _passwordPanel.IsVisible = false;
        try
        {
            var chains = await Task.Run(() => ImageCatalog.Chains(ImageCatalog.Scan(path, depth: 4)));
            _images = chains.SelectMany(c => c.Images.Select(i => (i, c))).OrderByDescending(x => x.i.Header.CreatedUtc).Select(x => x.i).Take(40).ToList();
            var complete = chains.ToDictionary(c => c.ChainId, c => c.Images);
            _status.Text = _images.Count == 0 ? "No backups in that location." : "";
            foreach (var img in _images)
            {
                var h = img.Header;
                bool ok = complete.TryGetValue(h.ChainId, out var set) && Enumerable.Range(0, h.ChainOrdinal + 1).All(k => set.Any(s => s.Header.ChainOrdinal == k));
                var pill = img.Damaged ? Ui.Pill("Damaged", "#FEE2E2", "#991B1B")
                    : !ok ? Ui.Pill("Missing files", "#FEF3C7", "#92400E")
                    : h.IsEncrypted ? Ui.Pill("🔒 Encrypted", "#EDE9FE", "#5B21B6")
                    : Ui.Pill("Ready", "#DCFCE7", "#166534");
                var row = Ui.Columns("*,Auto",
                    Ui.Stack(2,
                        Ui.Text($"{h.ComputerName} · {When(h.CreatedUtc)}", 14, FontWeight.SemiBold),
                        Ui.Hint($"{h.KindText} · {h.JobName} · {h.DiskModel} ({Util.FormatBytes(h.DiskSize)})")),
                    pill);
                var image = img;
                var card = Ui.Card(row, () => Pick(image), disabled: img.Damaged || !ok);
                card.Tag = img;
                _points.Children.Add(card);
            }
        }
        catch (Exception ex) { _status.Text = "Couldn't read that location: " + ex.Message; }
    }

    private static string When(DateTime utc)
    {
        var l = utc.ToLocalTime();
        if (l.Date == DateTime.Today) return $"Today {l:HH:mm}";
        if (l.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {l:HH:mm}";
        return l.ToString("ddd d MMM yyyy, HH:mm");
    }

    private void Pick(ImageEntry img)
    {
        _picked = img;
        foreach (var c in _points.Children.OfType<Border>())
        {
            c.Classes.Remove("selected");
            if (c.Tag == img) c.Classes.Add("selected");
        }
        _passwordPanel.IsVisible = img.Header.IsEncrypted;
        _error.IsVisible = false;
        _next.IsEnabled = true;
        if (img.Header.IsEncrypted) _imagePassword.Focus();
    }

    private async Task ConnectShare()
    {
        string share = (_share.Text ?? "").Trim();
        if (!share.StartsWith(@"\\")) { ShowError(@"Enter the share like \\NAS\Backups."); return; }
        _connect.IsEnabled = false;
        _error.IsVisible = false;
        try
        {
            string user = (_user.Text ?? "").Trim(), pass = _pass.Text ?? "";
            string path = await Task.Run(() => _w.Platform.ConnectShare(share, user.Length > 0 ? user : null, pass, _w.Log));
            int n = await Task.Run(() => ImageCatalog.Scan(path, depth: 4).Count);
            AddLocation(new ImageLocation(share, path, "nas", n));
            await ShowImages(path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _connect.IsEnabled = true; }
    }

    private async Task Next()
    {
        if (_picked == null) return;
        string password = "";
        if (_picked.Header.IsEncrypted)
        {
            password = _imagePassword.Text ?? "";
            _next.IsEnabled = false;
            try
            {
                var info = _picked.Header.Encryption!;
                await Task.Run(() => ImageKey.Open(password, info).Dispose());
            }
            catch (WrongPasswordException ex) { ShowError(ex.Message); _next.IsEnabled = true; return; }
            catch (Exception ex) { ShowError(ex.Message); _next.IsEnabled = true; return; }
        }
        _w.State.Image = _picked;
        _w.State.Password = password;
        if (_w.State.BrowseMode) _w.Show(new BrowsePage(_w), 2);
        else _w.Show(new ChooseDiskPage(_w), 2);
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }
}
