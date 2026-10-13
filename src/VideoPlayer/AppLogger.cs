using Avalonia.Threading;
using System.Text;

namespace VideoPlayer;

internal static class AppLogger
{
    private static readonly object Sync = new();
    public static string LogDirectory => AppStoragePaths.LogDirectory;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Error("Unhandled process exception", eventArgs.ExceptionObject as Exception
                ?? new Exception(eventArgs.ExceptionObject?.ToString() ?? "Unknown exception"));

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Error("Unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            Error("Unhandled UI exception", eventArgs.Exception);

        Information("Application started");
    }

    public static void Error(string context, Exception exception) => Write("ERROR", context, exception);

    public static void Information(string message) => Write("INFO", message, null);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"Pantheon-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [").Append(level).Append("] ")
                .AppendLine(message);
            if (exception is not null) entry.AppendLine(exception.ToString());

            lock (Sync)
                File.AppendAllText(logPath, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must not cause another application failure.
        }
    }
}
