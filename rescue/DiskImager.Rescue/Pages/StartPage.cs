using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DiskImager.Rescue.Pages;

/// <summary>"What do you want to do?"</summary>
public sealed class StartPage : UserControl
{
    public StartPage(MainWindow w)
    {
        Control Choice(string emoji, string tint, string title, string text, Action go) => Ui.Card(
            Ui.Columns("Auto,*",
                Ui.IconTile(emoji, tint, 46),
                new StackPanel { Margin = new Avalonia.Thickness(14, 0, 0, 0), Spacing = 3, Children = { Ui.Text(title, 16, FontWeight.SemiBold), Ui.Hint(text) } }),
            go);

        var grid = new UniformGrid { Columns = 2, Margin = new Avalonia.Thickness(-6) };
        foreach (var c in new[]
        {
            Choice("🔁", "#DBEAFE", "Restore a backup", "Put a whole disk back: after a dead drive, broken Windows, or onto a new drive.",
                () => { w.State.BrowseMode = false; w.Show(new FindBackupPage(w), 1); }),
            Choice("📂", "#FEF3C7", "Browse and copy files", "Open a backup and copy a few files or folders to a USB drive.",
                () => { w.State.BrowseMode = true; w.Show(new FindBackupPage(w), 1); }),
            Choice("🩺", "#DCFCE7", "Check disk health", "See whether this PC's drives report problems or wear.",
                () => w.Show(new HealthPage(w))),
            Choice("🛠", "#F1F5F9", "Network and tools", "Wi-Fi and network settings, all disks, save the log.",
                () => w.Show(new ToolsPage(w))),
        })
        {
            c.Margin = new Avalonia.Thickness(6);
            grid.Children.Add(c);
        }

        Content = Ui.Stack(0,
            Ui.Title("What do you want to do?"),
            Ui.Subtitle("This PC started from the DiskImager rescue USB. Nothing on its disks changes until you confirm."),
            grid,
            Ui.Banner("Tip: the desktop behind this window also has Firefox, LibreOffice, Tailscale and remote support tools."));
    }
}
