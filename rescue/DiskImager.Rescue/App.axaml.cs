using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DiskImager.Rescue;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow(Program.Platform);
            desktop.MainWindow = w;
            if (Program.AutotestShots != null)
                w.Opened += async (_, _) =>
                {
                    int code = await DemoAutoTest.Run(w, Program.AutotestShots, Program.AutotestManifest, Program.AutotestPassword);
                    desktop.Shutdown(code);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
