using Avalonia;
using Avalonia.Win32;

namespace VideoPlayer;

internal static class Program
{
    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }
    internal static IReadOnlyList<string> StartupVideos { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Initialize();
        StartupVideos = GetStartupVideos(args);
        if (OperatingSystem.IsWindows())
        {
            if (!SingleInstanceCoordinator.TryStartOrForward(StartupVideos, out var singleInstance))
                return;
            SingleInstance = singleInstance;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Application startup failed", exception);
            throw;
        }
        finally
        {
            SingleInstance?.Dispose();
            SingleInstance = null;
        }
    }

    internal static IReadOnlyList<string> GetStartupVideos(IEnumerable<string> args) =>
        VideoPlayer.Core.MediaPathRules.ResolveExistingVideoPaths(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            });
        }
        return builder;
    }
}
