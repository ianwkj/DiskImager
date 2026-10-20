using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskImager;

/// <summary>
/// DiskImager's own message box (same card style as the update and "already running" pop-ups), used everywhere
/// instead of the plain Windows one: <see cref="AppMessage.Show(Window?, string, string, MessageBoxButton, MessageBoxImage)"/>.
/// The first paragraph of the message is the heading; the rest is the detail, which can be selected and copied.
/// </summary>
public partial class MessageWindow : Window
{
    public MessageBoxResult Result { get; private set; }

    public MessageWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        Title = caption.Length > 0 ? caption : "DiskImager";

        // First paragraph = heading, the rest = detail.
        string text = message.Replace("\r\n", "\n").Trim();
        int split = text.IndexOf("\n\n", StringComparison.Ordinal);
        string heading = split < 0 ? text : text[..split].Trim();
        string body = split < 0 ? "" : text[(split + 2)..].Trim();
        if (split < 0 && text.Length > 140) { heading = ""; body = text; } // one long paragraph reads better as body text
        CaptionText.Text = (caption.Length > 0 ? caption : "DiskImager").ToUpperInvariant();
        HeadingText.Text = heading;
        HeadingText.Visibility = heading.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyText.Text = body;
        BodyText.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (heading.Length == 0) BodyText.Margin = new Thickness(-2, 4, 0, 0);

        var (bg, fg, glyph) = image switch
        {
            MessageBoxImage.Error => ("#FEE2E2", "#B91C1C", ""),
            MessageBoxImage.Warning => ("#FEF3C7", "#B45309", ""),
            MessageBoxImage.Question => ("#DBEAFE", "#1D4ED8", ""),
            _ => ("#DBEAFE", "#1D4ED8", ""),
        };
        IconTile.Background = DiskBars.Hex(bg);
        IconGlyph.Foreground = DiskBars.Hex(fg);
        IconGlyph.Text = glyph;

        // Buttons, the safe choice last (and on Esc). A risky "Yes" (a warning) is red.
        bool risky = image == MessageBoxImage.Warning;
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes, risky ? "Danger" : "Primary", isDefault: !risky);
                Add("No", MessageBoxResult.No, null, isCancel: true, isDefault: risky);
                Result = MessageBoxResult.No;
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes, risky ? "Danger" : "Primary", isDefault: !risky);
                Add("No", MessageBoxResult.No, null);
                Add("Cancel", MessageBoxResult.Cancel, null, isCancel: true, isDefault: risky);
                Result = MessageBoxResult.Cancel;
                break;
            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK, risky ? "Danger" : "Primary", isDefault: !risky);
                Add("Cancel", MessageBoxResult.Cancel, null, isCancel: true, isDefault: risky);
                Result = MessageBoxResult.Cancel;
                break;
            default:
                Add("OK", MessageBoxResult.OK, "Primary", isDefault: true, isCancel: true);
                Result = MessageBoxResult.OK;
                break;
        }
        ((Button)Buttons.Children[^1]).Margin = new Thickness(0);
        Loaded += (_, _) =>
        {
            Activate();
            (Buttons.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? (Button)Buttons.Children[0]).Focus();
        };
    }

    private void Add(string text, MessageBoxResult result, string? style, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 84 };
        if (style != null) b.Style = (Style)FindResource(style);
        b.Click += (_, _) => { Result = result; Close(); };
        Buttons.Children.Add(b);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}

/// <summary>Drop-in for MessageBox.Show with DiskImager's own look.</summary>
public static class AppMessage
{
    public static MessageBoxResult Show(Window? owner, string message, string caption = "DiskImager",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information)
    {
        var w = new MessageWindow(message, caption, buttons, image);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive && x != w);
        if (owner != null && owner.IsVisible)
            w.Owner = owner;
        else
        {
            // No window to sit over (tray helper, uninstall, start-up): its own taskbar button, on top, centred.
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowInTaskbar = true;
            w.Topmost = true;
        }
        w.ShowDialog();
        return w.Result;
    }

    public static MessageBoxResult Show(string message, string caption = "DiskImager",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information) =>
        Show(null, message, caption, buttons, image);
}
