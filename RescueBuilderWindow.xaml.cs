using System.Windows;
using DiskImager.Core;
using Microsoft.Win32;

namespace DiskImager;

/// <summary>Creates the rescue USB stick or ISO (see <see cref="RescueMedia"/>).</summary>
public partial class RescueBuilderWindow : Window
{
    private readonly Logger _log;
    private List<DiskInfo> _usb = new();
    private bool _busy;

    public RescueBuilderWindow(Logger log)
    {
        InitializeComponent();
        _log = log;
        IsoBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TechSource-Rescue.iso");
        Loaded += async (_, _) => await LoadUsb();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private async Task LoadUsb()
    {
        UsbList.Items.Clear();
        _usb = await Task.Run(() => DiskEnumerator.GetDisks()
            .Where(d => !d.IsSystem && d.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) && d.Size >= (900L << 20))
            .ToList());
        foreach (var d in _usb) UsbList.Items.Add(DiskBars.Card(d, compact: true));
        if (_usb.Count > 0) UsbList.SelectedIndex = 0;
        NoUsbText.Visibility = _usb.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadUsb();

    private void Target_Click(object sender, RoutedEventArgs e)
    {
        bool iso = IsoRadio.IsChecked == true;
        IsoPanel.IsEnabled = iso;
        UsbList.IsEnabled = !iso;
        CreateButton.Style = (Style)FindResource(iso ? "Primary" : "Danger");
    }

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        bool linux = LinuxRadio.IsChecked == true;
        OptionsGroup.Visibility = linux ? Visibility.Collapsed : Visibility.Visible;
        IsoBox.Text = Path.Combine(Path.GetDirectoryName(IsoBox.Text) ?? "", linux ? "TechSource-Rescue.iso" : "DiskImager-Rescue.iso");
    }

    private void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Save the rescue ISO as", Filter = "ISO image (*.iso)|*.iso", FileName = Path.GetFileName(IsoBox.Text) };
        if (dlg.ShowDialog(this) == true) IsoBox.Text = dlg.FileName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        bool iso = IsoRadio.IsChecked == true;
        DiskInfo? usb = null;
        if (!iso)
        {
            if (UsbList.SelectedIndex < 0) { MessageBox.Show(this, "Choose the USB stick to use.", "Rescue USB"); return; }
            usb = _usb[UsbList.SelectedIndex];
            if (MessageBox.Show(this, $"Everything on {usb.Title} will be erased.\n\nContinue?", "Erase USB stick",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        string? isoPath = iso ? IsoBox.Text.Trim() : null;
        if (iso && string.IsNullOrEmpty(isoPath)) return;

        _busy = true;
        CreateButton.IsEnabled = CloseButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<ProgressInfo>(p =>
        {
            StatusText.Text = p.Total > 0 && p.Done > 0
                ? $"{p.Stage}: {Util.FormatBytes(p.Done)} of {Util.FormatBytes(p.Total)}" + (p.BytesPerSecond > 0 ? $" ({Util.FormatBytes((long)p.BytesPerSecond)}/s)" : "")
                : p.Stage;
            Bar.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) Bar.Value = Math.Min(1, (double)p.Done / p.Total);
        });
        bool drivers = DriversBox.IsChecked == true;
        bool linux = LinuxRadio.IsChecked == true;
        try
        {
            if (linux)
            {
                StatusText.Text = "Looking for the latest rescue system…";
                var release = await RescueImage.FindLatest()
                    ?? throw new InvalidOperationException("No TechSource Rescue image has been published yet. Choose the Windows recovery system instead.");
                string downloaded = await Task.Run(() => RescueImage.Download(release, progress, _log));
                if (usb != null) await Task.Run(() => RescueImage.WriteToUsb(downloaded, usb, progress, CancellationToken.None, _log));
                else await Task.Run(() => File.Copy(downloaded, isoPath!, overwrite: true));
            }
            else
                await Task.Run(() => RescueMedia.Build(new RescueMedia.Options { UsbDisk = usb, IsoPath = isoPath, AddDrivers = drivers }, progress, CancellationToken.None, _log));
            StatusText.Text = "Done.";
            MessageBox.Show(this, iso
                    ? $"The rescue ISO is ready:\n{isoPath}\n\nBoot a server or virtual machine from it, or burn it to a DVD."
                    : "The rescue USB stick is ready.\n\nTo use it: plug it into the PC, turn the PC on and open its boot menu (often F12, F11 or Esc), then choose the USB stick.",
                "Rescue media", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            _log.Error("Rescue media: " + ex);
            StatusText.Text = "Couldn't create the rescue media: " + ex.Message;
            MessageBox.Show(this, "Couldn't create the rescue media:\n\n" + ex.Message, "Rescue media", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            CreateButton.IsEnabled = CloseButton.IsEnabled = true;
        }
    }
}
