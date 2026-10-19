using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;

namespace DiskImagerSetup
{
    public partial class SetupWindow : Window
    {
        private readonly InstallTarget _target;
        private bool _installed;

        public SetupWindow(InstallTarget target)
        {
            InitializeComponent();
            _target = target;
            string version = Installer.Version;
            string existing = Installer.InstalledVersion(target);
            VersionText.Text = "Version " + version;
            LocationText.Text = target.InstallDir;
            LocationText.ToolTip = target.InstallDir;

            if (existing == null)
            {
                WelcomeTitle.Text = "Install DiskImager";
                WelcomeText.Text = "DiskImager backs up whole disks as images, runs scheduled full and incremental backups to a local drive or NAS, and restores or clones disks. Click Install to continue.";
            }
            else
            {
                WelcomeTitle.Text = existing == version ? "Reinstall DiskImager" : "Update DiskImager";
                WelcomeText.Text = existing == version
                    ? $"DiskImager {version} is already installed. Reinstalling keeps your backup jobs, settings and backups."
                    : $"DiskImager {existing} is installed. Updating to {version} keeps your backup jobs, settings and backups, and updates your scheduled backups.";
                InstallButton.Content = existing == version ? "Reinstall" : "Update";
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            WelcomePage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Collapsed;
            ProgressPage.Visibility = Visibility.Visible;
            bool desktop = DesktopBox.IsChecked == true, startMenu = StartMenuBox.IsChecked == true;
            try
            {
                await Task.Run(() => Installer.Install(_target, desktop, startMenu, (msg, frac) =>
                    Dispatcher.BeginInvoke(new Action(() => { StatusText.Text = msg; Bar.Value = frac; }))));
                _installed = true;
                ShowDone(true, "DiskImager is installed",
                    "You'll find it in the Start menu" + (desktop ? " and on your desktop" : "") + ". " +
                    "Scheduled backups run in the background whether or not DiskImager is open.");
            }
            catch (RetryableException ex)
            {
                ShowDone(false, "Setup needs a moment", ex.Message, retry: true);
            }
            catch (Exception ex)
            {
                ShowDone(false, "DiskImager couldn't be installed", ex.Message, retry: true);
            }
        }

        private void ShowDone(bool ok, string title, string text, bool retry = false)
        {
            ProgressPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
            DoneTitle.Text = title;
            DoneText.Text = text;
            DoneIcon.Text = ok ? "✓" : "!";
            DoneIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#16A34A" : "#D97706"));
            DoneBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#DCFCE7" : "#FEF3C7"));
            RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            FinishButton.Content = ok ? "Finish" : "Close";
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (_installed && LaunchBox.IsChecked == true && _target.RunPostInstall)
            {
                try { Process.Start(new ProcessStartInfo(_target.ExePath) { UseShellExecute = true }); } catch { }
            }
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // Opened via Explorer so the browser doesn't inherit setup's admin rights.
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + e.Uri.AbsoluteUri + "\"") { UseShellExecute = false }); } catch { }
            e.Handled = true;
        }
    }
}
