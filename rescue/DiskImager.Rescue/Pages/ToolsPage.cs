using Avalonia.Controls;
using Avalonia.Media;
using DiskImager.Core;

namespace DiskImager.Rescue.Pages;

/// <summary>Network settings, every disk at a glance, saving the log, restart and shut down.</summary>
public sealed class ToolsPage : UserControl
{
    private readonly StackPanel _disks = new() { Spacing = 10 };
    private readonly StackPanel _logResult = new();

    public ToolsPage(MainWindow w)
    {
        Content = Ui.Stack(0,
            Ui.Title("Network and tools"),
            Ui.Subtitle("Connect to Wi-Fi or check the network, look at every disk in this PC, or save DiskImager's log for support."),
            Ui.Card(Ui.Columns("*,Auto",
                Ui.Stack(2, Ui.Text("Network", 15, FontWeight.SemiBold), Ui.Hint(w.Platform.NetworkSummary())),
                Ui.Button("Wi-Fi and network settings", w.Platform.OpenNetworkSettings))),
            new Border { Height = 10 },
            Ui.Card(Ui.Columns("*,Auto",
                Ui.Stack(2, Ui.Text("DiskImager log", 15, FontWeight.SemiBold), Ui.Hint("Everything this rescue session did, for TechSource Digital support.")),
                Ui.Button("Save to USB drive", async () => await SaveLog(w)))),
            _logResult,
            new Border { Height = 18 },
            Ui.Label("Disks in this PC"),
            _disks,
            Ui.ButtonRow(Ui.Button("Restart", w.Platform.Reboot), Ui.Button("Shut down", w.Platform.PowerOff), Ui.Button("Back", () => w.GoHome(), "primary")));
        AttachedToVisualTree += async (_, _) =>
        {
            try
            {
                foreach (var d in await Task.Run(w.Platform.GetDisks))
                    _disks.Children.Add(Ui.Card(Ui.Stack(8,
                        Ui.Columns("*,Auto", Ui.Text($"Disk {d.Number} · {d.Model}{(d.IsSystem ? "  (rescue stick)" : "")}", 14, FontWeight.SemiBold), Ui.Text(Util.FormatBytes(d.Size), 14, FontWeight.SemiBold)),
                        Ui.DiskBar(d))));
            }
            catch (Exception ex) { _disks.Children.Add(Ui.Banner("Couldn't read the disks: " + ex.Message, danger: true)); }
        };
    }

    private async Task SaveLog(MainWindow w)
    {
        _logResult.Children.Clear();
        var drives = await Task.Run(() => w.Platform.WritableDrives(w.Log));
        if (drives.Count == 0) { _logResult.Children.Add(Ui.Banner("Plug in a USB drive first.")); return; }
        try
        {
            string file = Path.Combine(drives[0].Path, $"DiskImager-rescue-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.Copy(w.Log.FilePath, file, overwrite: true);
            _logResult.Children.Add(Ui.Banner($"Saved to {drives[0].Name}: {Path.GetFileName(file)}", success: true));
        }
        catch (Exception ex) { _logResult.Children.Add(Ui.Banner("Couldn't save the log: " + ex.Message, danger: true)); }
    }
}
