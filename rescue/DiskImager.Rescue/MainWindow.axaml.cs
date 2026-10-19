using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DiskImager.Core;
using DiskImager.Rescue.Pages;
using DiskImager.Rescue.Platform;

namespace DiskImager.Rescue;

/// <summary>What the user has chosen so far in a restore or browse.</summary>
public sealed class RescueState
{
    public bool BrowseMode { get; set; }
    public ImageEntry? Image { get; set; }
    public string Password { get; set; } = "";
    public DiskInfo? Target { get; set; }
    public bool KeepIds { get; set; } = true;
}

/// <summary>The rescue app's one window: a top bar, step chips, and the current page.</summary>
public partial class MainWindow : Window
{
    public IRescuePlatform Platform { get; }
    public Logger Log { get; } = new();
    public RescueState State { get; private set; } = new();
    /// <summary>True while a restore or copy is running (navigation and power are blocked).</summary>
    public bool Busy { get; set; }

    private static readonly string[] RestoreSteps = { "Start", "Find backup", "Choose disk", "Confirm", "Restore" };
    private static readonly string[] BrowseSteps = { "Start", "Find backup", "Browse files" };

    public MainWindow() : this(new DemoPlatform(Path.Combine(Path.GetTempPath(), "diskimager-rescue-demo"))) { }

    public MainWindow(IRescuePlatform platform)
    {
        InitializeComponent();
        Platform = platform;
        try { Logo.Source = new Bitmap(AssetLoader.Open(new Uri("avares://diskimager-rescue/Assets/logo.png"))); } catch { }
        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = v != null ? $"Version {v.Major}.{v.Minor}.{v.Build}" + (platform is DemoPlatform ? " · demo" : "") : "";
        HomeButton.Click += (_, _) => { if (!Busy) GoHome(); };
        RestartItem.Click += (_, _) => { if (!Busy) Platform.Reboot(); };
        ShutdownItem.Click += (_, _) => { if (!Busy) Platform.PowerOff(); };
        RefreshNetwork();
        DispatcherTimer.Run(() => { RefreshNetwork(); return true; }, TimeSpan.FromSeconds(5));
        Log.Info($"DiskImager Rescue started ({platform.Name}).");
        GoHome();
    }

    private void RefreshNetwork()
    {
        try { NetText.Text = "🖧  " + Platform.NetworkSummary(); } catch { NetText.Text = ""; }
    }

    public void GoHome()
    {
        State = new RescueState();
        Show(new StartPage(this));
    }

    /// <summary>Shows a page; step = its position in the restore/browse flow (-1 = no step chips).</summary>
    public void Show(Control page, int step = -1)
    {
        Host.Content = page;
        Scroller.Offset = default;
        StepsBar.IsVisible = step >= 0;
        Steps.Children.Clear();
        if (step < 0) return;
        var names = State.BrowseMode ? BrowseSteps : RestoreSteps;
        for (int i = 0; i < names.Length; i++)
            Steps.Children.Add(Ui.StepChip(names[i], i < step ? Ui.StepState.Done : i == step ? Ui.StepState.Current : Ui.StepState.Todo));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Busy) e.Cancel = true; // never close in the middle of a restore
        base.OnClosing(e);
    }
}
