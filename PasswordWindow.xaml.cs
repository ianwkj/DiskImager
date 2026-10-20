using System.Windows;
using DiskImager.Core;

namespace DiskImager;

/// <summary>Asks for an encrypted backup's password and checks it before closing.</summary>
public partial class PasswordWindow : Window
{
    private readonly EncryptionInfo _info;

    public string Password { get; private set; } = "";

    public PasswordWindow(ImageHeader header)
    {
        InitializeComponent();
        ScreenFit.Attach(this);
        _info = header.Encryption!;
        DetailText.Text = $"Enter the password for the {header.JobName} backup from {header.ComputerName} ({header.CreatedUtc.ToLocalTime():g}).";
        Loaded += (_, _) => PassBox.Focus();
        PassBox.PasswordChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        string pw = PassBox.Password;
        OkButton.IsEnabled = false;
        try
        {
            // Deriving the key takes a moment (that's what makes guessing passwords slow).
            await Task.Run(() => ImageKey.Open(pw, _info).Dispose());
            Password = pw;
            DialogResult = true;
        }
        catch (WrongPasswordException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            PassBox.SelectAll();
            PassBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }
}
