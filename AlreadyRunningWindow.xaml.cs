using System.Windows;
using System.Windows.Input;

namespace DiskImager;

/// <summary>Shown when DiskImager is opened while its window is already open (only one is allowed at a time).</summary>
public partial class AlreadyRunningWindow : Window
{
    /// <summary>True when the user chose to switch to the open window.</summary>
    public bool SwitchChosen { get; private set; }

    /// <param name="canSwitch">The open window is in this session, so it can be brought to the front.</param>
    public AlreadyRunningWindow(bool canSwitch)
    {
        InitializeComponent();
        if (canSwitch)
        {
            DetailText.Text = "Only one DiskImager window can be open at a time. Switch to the one that's already open.";
        }
        else
        {
            DetailText.Text = "Only one DiskImager window can be open at a time. It may be open in another user's session on this PC, " +
                              "or still closing. Try again in a moment.";
            SwitchButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = "OK";
            CloseButton.Style = (Style)FindResource("Primary");
            CloseButton.IsDefault = true;
        }
        Loaded += (_, _) => Activate();
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        SwitchChosen = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
