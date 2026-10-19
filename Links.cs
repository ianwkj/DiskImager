using System.Diagnostics;
using System.Windows.Navigation;

namespace DiskImager;

public static class Links
{
    public const string TechSource = "https://e-techsource.com";

    /// <summary>
    /// Opens a web link in the user's normal browser. Handing it to Explorer (rather than starting the
    /// browser ourselves) means it opens without the admin rights the DiskImager window runs with.
    /// </summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false }); }
        catch { }
    }

    public static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
