using System.IO.Pipes;
using System.Text;

namespace VideoPlayer;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static string MutexName => AppStoragePaths.InstanceName;
    private static string PipeName => AppStoragePaths.InstanceName;
    private const int ForwardAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listenerTask;

    public event Action<IReadOnlyList<string>>? VideosRequested;

    private SingleInstanceCoordinator(Mutex mutex)
    {
        _mutex = mutex;
        _listenerTask = Task.Run(ListenAsync);
    }

    public static bool TryStartOrForward(IReadOnlyList<string> videoPaths, out SingleInstanceCoordinator? coordinator)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (ownsMutex)
            {
                coordinator = new SingleInstanceCoordinator(mutex);
                return true;
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        mutex.Dispose();
        ForwardToPrimary(videoPaths);
        coordinator = null;
        return false;
    }

    private static void ForwardToPrimary(IReadOnlyList<string> videoPaths)
    {
        for (var attempt = 0; attempt < ForwardAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(250);
                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (videoPaths.Count == 0)
                {
                    writer.WriteLine(string.Empty);
                }
                else
                {
                    foreach (var videoPath in videoPaths)
                        writer.WriteLine(videoPath);
                }
                writer.Flush();
                return;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (IOException)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        AppLogger.Error(
            "Could not forward the open request to the running Pantheon instance",
            new IOException("The named pipe did not become available before the retry limit."));
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var firstPath = await reader.ReadLineAsync(_shutdown.Token);
                if (firstPath is null) continue;
                if (string.IsNullOrWhiteSpace(firstPath))
                {
                    VideosRequested?.Invoke(Array.Empty<string>());
                    continue;
                }

                var videoPaths = new List<string> { firstPath };
                while (await reader.ReadLineAsync(_shutdown.Token) is { } path)
                    if (!string.IsNullOrWhiteSpace(path)) videoPaths.Add(path);
                VideosRequested?.Invoke(videoPaths);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException exception) when (!_shutdown.IsCancellationRequested)
            {
                AppLogger.Error("Single-instance message channel failed", exception);
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listenerTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(error => error is OperationCanceledException))
        {
        }

        _shutdown.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
