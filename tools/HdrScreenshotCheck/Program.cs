using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Win32;
using HanumanInstitute.LibMpv;
using VideoPlayer;

internal static class Program
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly double[] SamplePositions = [60, 300, 1800];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/HdrScreenshotCheck -- <HDR-video-path> <output-directory>");
            return 2;
        }

        var outputDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outputDirectory);
        // Keep the test isolated from the user's preferences and playback history.
        Environment.SetEnvironmentVariable("PANTHEON_DATA_DIRECTORY", Path.Combine(outputDirectory, "profile"));
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            })
            .AfterSetup(builder =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
                lifetime.Startup += async (_, _) =>
                {
                    try
                    {
                        await VerifyAsync(lifetime.MainWindow!, Path.GetFullPath(args[0]), outputDirectory);
                        Console.WriteLine("PASS: HDR tone mapping, source resolution, paused/playing captures, subtitle exclusion and media switching.");
                        lifetime.Shutdown();
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine(exception);
                        lifetime.Shutdown(1);
                    }
                };
            }).StartWithClassicDesktopLifetime(args);
    }

    private static async Task VerifyAsync(Window window, string videoPath, string outputDirectory)
    {
        await Task.Delay(500);
        var surface = window.FindControl<ToneMappedVideoView>("VideoSurface")!;
        var player = surface.MpvContext;
        player.SetProperty("audio", false);
        player.SetProperty("pause", true);
        player.LoadFile(videoPath).Invoke();
        await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
        var transfer = player.GetProperty<string>("video-params/gamma");
        Require(transfer is "pq" or "hlg", $"Expected HDR input, received {transfer}.");
        Require(player.GetProperty<double>("duration") > SamplePositions[0] + 1, "Use an HDR video longer than 61 seconds.");
        Console.WriteLine($"Input: {transfer}, renderer: {player.GetProperty<string>("current-vo")}");

        foreach (var position in SamplePositions)
        {
            if (position >= player.GetProperty<double>("duration")) continue;
            player.Seek(position, SeekOption.Absolute | SeekOption.Exact).Invoke();
            await WaitUntilAsync(() => !player.GetProperty<bool>("seeking")
                && Math.Abs(player.GetProperty<double>("time-pos") - position) < 0.1);
            var capture = Path.Combine(outputDirectory, $"frame-{position:0}.png");
            await surface.CaptureFrameAsync(capture);
            ValidatePng(capture, player.GetProperty<int>("video-out-params/w"), player.GetProperty<int>("video-out-params/h"));
            Console.WriteLine($"PASS: {position:0}s, SDR color metadata and full source resolution.");
        }

        var subtitlePath = Path.Combine(outputDirectory, "capture-check.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,000 --> 23:59:59,000\nHDR screenshot subtitle exclusion check\n");
        player.SetProperty("sub-visibility", true);
        player.SubAdd(subtitlePath, LoadOption.Select).Invoke();
        await WaitUntilAsync(() => player.GetProperty<string>("sub-text").Contains("subtitle exclusion", StringComparison.Ordinal));
        var withSubtitles = Path.Combine(outputDirectory, "with-subtitles.png");
        var withoutSubtitles = Path.Combine(outputDirectory, "without-subtitles.png");
        var hiddenSubtitles = Path.Combine(outputDirectory, "hidden-subtitles.png");
        await surface.CaptureFrameAsync(withSubtitles, includeSubtitles: true);
        await surface.CaptureFrameAsync(withoutSubtitles);
        player.SetProperty("sub-visibility", false);
        await surface.CaptureFrameAsync(hiddenSubtitles);
        Require(!Hash(withSubtitles).SequenceEqual(Hash(withoutSubtitles)), "The subtitle reference must contain visible subtitles.");
        Require(Hash(withoutSubtitles).SequenceEqual(Hash(hiddenSubtitles)), "Video screenshots must exclude subtitles.");

        var beforePlayback = player.GetProperty<double>("time-pos");
        player.SetProperty("pause", false);
        await WaitUntilAsync(() => player.GetProperty<double>("time-pos") > beforePlayback + 0.2);
        await surface.CaptureFrameAsync(Path.Combine(outputDirectory, "playing.png"));
        Require(!player.GetProperty<bool>("pause"), "Capture must restore active playback.");
        await WaitUntilAsync(() => player.GetProperty<double>("time-pos") > beforePlayback + 1);
        player.SetProperty("pause", true);

        // Wait for the new file event; duration can still describe the previous file.
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FileLoaded += (_, _) => reloaded.TrySetResult();
        player.Stop().Invoke();
        player.LoadFile(videoPath).Invoke();
        await reloaded.Task.WaitAsync(OperationTimeout);
        await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
        player.Seek(60, SeekOption.Absolute | SeekOption.Exact).Invoke();
        await WaitUntilAsync(() => !player.GetProperty<bool>("seeking") && player.GetProperty<double>("time-pos") >= 59.9);
        window.Width += 80;
        await Task.Delay(200);
        await surface.CaptureFrameAsync(Path.Combine(outputDirectory, "reloaded.png"));
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static void ValidatePng(string path, int width, int height)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Require(reader.ReadBytes(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Invalid PNG signature.");
        var isSrgb = false;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
            var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (type == "IHDR")
            {
                var header = reader.ReadBytes(length);
                Require(BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4)) == width
                    && BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4)) == height, "Source resolution was lost.");
                Require(header[8] == 8, "Expected a standard SDR PNG.");
            }
            else if (type == "cICP")
            {
                var colors = reader.ReadBytes(length);
                Require(colors[0] == 1 && colors[1] == 13, "HDR colors leaked into the SDR screenshot.");
                isSrgb = true;
            }
            else
            {
                if (type == "sRGB") isSrgb = true;
                reader.BaseStream.Seek(length, SeekOrigin.Current);
            }
            reader.BaseStream.Seek(4, SeekOrigin.Current); // Chunk CRC.
        }
        Require(isSrgb, "Screenshot is missing sRGB color metadata.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < OperationTimeout)
        {
            try { if (condition()) return; }
            catch (MpvException) { } // Properties are unavailable during load/seek.
            await Task.Delay(50);
        }
        throw new TimeoutException("The renderer did not complete the requested operation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
