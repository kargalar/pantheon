using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Globalization;

namespace VideoPlayer;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        var interfaceCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = interfaceCulture;
        CultureInfo.DefaultThreadCurrentUICulture = interfaceCulture;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow(Program.StartupVideos);
            if (Program.SingleInstance is { } singleInstance)
                singleInstance.VideosRequested += mainWindow.OpenVideosFromAnotherInstance;
            desktop.MainWindow = mainWindow;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
