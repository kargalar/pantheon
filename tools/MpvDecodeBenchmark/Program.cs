using System.Diagnostics;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: dotnet run -c Release --project tools/MpvDecodeBenchmark -- <video-path> <hwdec:auto|no> [sample-seconds]");
    return 2;
}

var videoPath = Path.GetFullPath(args[0]);
if (!File.Exists(videoPath)) throw new FileNotFoundException("Video file was not found.", videoPath);
if (args[1] is not ("auto" or "no")) throw new ArgumentException("hwdec must be 'auto' or 'no'.", nameof(args));

var sampleSeconds = args.Length == 3
    ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
    : 8;
if (sampleSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(args), "Sample duration must be between 1 and 120 seconds.");

using var player = new MpvContext();
var endFile = new TaskCompletionSource<MpvEndFileEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
player.EndFile += (_, eventArgs) => endFile.TrySetResult(eventArgs);
player.Initialize();
player.SetProperty("hwdec", args[1]);
player.SetProperty("vo", "null");
player.LoadFile(videoPath).Invoke();

await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0, TimeSpan.FromSeconds(15));
var duration = player.GetProperty<double>("duration");
if (duration < sampleSeconds + 1)
    throw new InvalidOperationException($"Video duration ({duration:F1}s) must exceed the sample duration ({sampleSeconds}s) by at least one second.");

var process = Process.GetCurrentProcess();
var cpuBefore = process.TotalProcessorTime;
var workingSetBefore = process.WorkingSet64;
var stopwatch = Stopwatch.StartNew();
await Task.Delay(TimeSpan.FromSeconds(sampleSeconds));
stopwatch.Stop();
process.Refresh();

if (endFile.Task.IsCompleted)
    throw new InvalidOperationException("Playback ended before the sample completed.");

string activeDecoder;
try { activeDecoder = player.GetProperty<string>("hwdec-current") ?? "unknown"; }
catch (MpvException) { activeDecoder = "unavailable"; }

var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
var cpuPercentOfOneCore = cpuSeconds / stopwatch.Elapsed.TotalSeconds * 100;
Console.WriteLine($"video={Path.GetFileName(videoPath)}, hwdec={args[1]}, hwdec-current={activeDecoder}");
Console.WriteLine($"sampleSeconds={stopwatch.Elapsed.TotalSeconds:F2}, cpuSeconds={cpuSeconds:F2}, cpuPercentOfOneCore={cpuPercentOfOneCore:F1}");
Console.WriteLine($"workingSetBeforeMiB={workingSetBefore / 1024d / 1024d:F1}, workingSetAfterMiB={process.WorkingSet64 / 1024d / 1024d:F1}");
return 0;

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (true)
    {
        try { if (condition()) return; }
        catch (MpvException) { }
        if (stopwatch.Elapsed >= timeout) throw new TimeoutException("libmpv did not finish opening the video in time.");
        await Task.Delay(50);
    }
}
