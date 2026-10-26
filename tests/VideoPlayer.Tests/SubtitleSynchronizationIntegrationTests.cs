using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HanumanInstitute.LibMpv;
using VideoPlayer;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed partial class SubtitleSynchronizationIntegrationTests
{
    [SubtitleToolsTheory]
    [InlineData(3.0, 1.0)]
    [InlineData(-1.5, 1.0)]
    [InlineData(2.0, 25.0 / 24.0)]
    public async Task RealSpeechAlignmentCorrectsOffsetAndDriftAndLoadsInMpv(double delay, double scale)
    {
        var executable = MainWindow.ResolveSubtitleSynchronizer(SubtitleAutomationOptions.DefaultSynchronizer);
        // This integration check requires tools/Build-SubtitleTools.ps1 or an FFsubsync executable on PATH.
        Assert.True(File.Exists(executable), "Build bundled tools before running speech integration tests: tools/Build-SubtitleTools.ps1");
        var video = Path.Combine(AppContext.BaseDirectory, "TestData", "speech.mkv");
        var reference = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "speech-reference.srt"));
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pantheon-speech-test-{Guid.NewGuid():N}")).FullName;
        var input = Path.Combine(directory, "shifted.srt");
        await File.WriteAllTextAsync(input, TimestampPattern().Replace(reference, match => FormatTime(ParseTime(match.Value) * scale + delay)));
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        try
        {
            var synchronizer = new SubtitleSynchronizer(new SubtitleFileCache(directory));
            var result = await synchronizer.SynchronizeAsync(video, input, executable, "0:a:1", "utf-8", default);
            Assert.False(result.FromCache);
            Assert.InRange(result.OffsetSeconds, -delay / scale - 0.45, -delay / scale + 0.45);
            Assert.InRange(result.ScaleFactor, 1 / scale - 0.01, 1 / scale + 0.01);
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
            var expectedTimes = TimestampPattern().Matches(reference).Select(match => ParseTime(match.Value)).ToArray();
            var actualTimes = TimestampPattern().Matches(await File.ReadAllTextAsync(result.Path)).Select(match => ParseTime(match.Value)).ToArray();
            Assert.Equal(expectedTimes.Length, actualTimes.Length);
            Assert.All(expectedTimes.Zip(actualTimes), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 0.45));

            using var player = new MpvContext();
            player.Initialize();
            player.LoadFile(video).Invoke();
            await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
            player.SetProperty("pause", true);
            player.SubAdd(result.Path, LoadOption.Select).Invoke();
            player.Seek(3.0, SeekOption.Absolute | SeekOption.Exact).Invoke();
            await WaitUntilAsync(() => (player.GetProperty<string>("sub-text") ?? "").Contains("train", StringComparison.Ordinal));
            Assert.Equal(0, player.GetProperty<double>("sub-delay"));
            var cached = await synchronizer.SynchronizeAsync(video, input, "missing-tool.exe", "0:a:1", "utf-8", default);
            Assert.True(cached.FromCache);
            Assert.Equal(result.Path, cached.Path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SubtitleToolsFact]
    public async Task SilentAudioCannotGenerateASynchronizedSubtitle()
    {
        var executable = MainWindow.ResolveSubtitleSynchronizer(SubtitleAutomationOptions.DefaultSynchronizer);
        Assert.True(File.Exists(executable), "Build bundled tools before running speech integration tests.");
        var video = Path.Combine(AppContext.BaseDirectory, "TestData", "speech.mkv");
        var input = Path.Combine(AppContext.BaseDirectory, "TestData", "speech-reference.srt");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pantheon-silence-{Guid.NewGuid():N}")).FullName;
        try
        {
            var sync = new SubtitleSynchronizer(new SubtitleFileCache(directory));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SynchronizeAsync(video, input,
                executable, "0:a:0", "utf-8", default));
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Empty(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SubtitleToolsTheory]
    [InlineData(".ass", "utf-8")]
    [InlineData(".ssa", "utf-8")]
    [InlineData(".vtt", "utf-8")]
    [InlineData(".srt", "windows-1254")]
    [InlineData(".srt", "infer")]
    public async Task TextFormatsAndTurkishCodepageProduceValidUtf8Srt(string extension, string encoding)
    {
        var executable = MainWindow.ResolveSubtitleSynchronizer(SubtitleAutomationOptions.DefaultSynchronizer);
        var video = Path.Combine(AppContext.BaseDirectory, "TestData", "speech.mkv");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pantheon-sub-format-{Guid.NewGuid():N}")).FullName;
        try
        {
            var reference = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "speech-reference.srt"));
            var inputSrt = Path.Combine(directory, "input.srt");
            var shifted = TimestampPattern().Replace(reference, match => FormatTime(ParseTime(match.Value) + 3)).Replace("The train", "Türkçe: İğüşöç The train");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var sourceCodepage = encoding == "infer" ? "windows-1254" : encoding;
            await File.WriteAllBytesAsync(inputSrt, Encoding.GetEncoding(sourceCodepage).GetBytes(shifted));
            var input = inputSrt;
            if (extension != ".srt")
            {
                input = Path.Combine(directory, "input" + extension);
                var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(executable)!, "ffmpeg.exe"))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", inputSrt, "-f", extension == ".vtt" ? "webvtt" : "ass", "-y", input })
                    start.ArgumentList.Add(argument);
                using var conversion = Process.Start(start)!;
                var errors = conversion.StandardError.ReadToEndAsync();
                await conversion.WaitForExitAsync();
                Assert.True(conversion.ExitCode == 0, await errors);
            }
            var result = await new SubtitleSynchronizer(new SubtitleFileCache(directory)).SynchronizeAsync(
                video, input, executable, "0:a:1", encoding, default);
            Assert.True(SubtitleFileCache.IsValidSrt(result.Path));
            Assert.InRange(result.OffsetSeconds, -3.45, -2.55);
            Assert.Contains("Türkçe: İğüşöç", await File.ReadAllTextAsync(result.Path, Encoding.UTF8));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SubtitleToolsFact]
    public async Task EmbeddedSubtitleReferenceStream_AlignsDirectlyWithHighPrecision()
    {
        var executable = MainWindow.ResolveSubtitleSynchronizer(SubtitleAutomationOptions.DefaultSynchronizer);
        var baseVideo = Path.Combine(AppContext.BaseDirectory, "TestData", "speech.mkv");
        var referenceSrtPath = Path.Combine(AppContext.BaseDirectory, "TestData", "speech-reference.srt");
        var referenceText = await File.ReadAllTextAsync(referenceSrtPath);
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pantheon-embedded-sub-{Guid.NewGuid():N}")).FullName;
        try
        {
            var videoWithEmbeddedSub = Path.Combine(directory, "video-with-sub.mkv");
            var ffmpeg = Path.Combine(Path.GetDirectoryName(executable)!, "ffmpeg.exe");
            var muxStart = new ProcessStartInfo(ffmpeg)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", baseVideo, "-i", referenceSrtPath,
                "-map", "0", "-map", "1:0",
                "-c", "copy", "-c:s", "srt",
                "-y", videoWithEmbeddedSub
            }) muxStart.ArgumentList.Add(arg);

            using (var mux = Process.Start(muxStart)!)
            {
                var err = await mux.StandardError.ReadToEndAsync();
                await mux.WaitForExitAsync();
                Assert.True(mux.ExitCode == 0, err);
            }

            var shiftedSrtPath = Path.Combine(directory, "shifted-tr.srt");
            const double delay = 4.2;
            var shiftedContent = "1\n00:00:00,500 --> 00:00:02,000\nÇeviri: Pantheon Test / @Pantheon\n\n"
                + TimestampPattern().Replace(referenceText, match => FormatTime(ParseTime(match.Value) + delay));
            await File.WriteAllTextAsync(shiftedSrtPath, shiftedContent, Encoding.UTF8);

            var sync = new SubtitleSynchronizer(new SubtitleFileCache(directory));
            var result = await sync.SynchronizeAsync(videoWithEmbeddedSub, shiftedSrtPath, executable, "0:s:0", "infer", default);

            Assert.False(result.FromCache);
            Assert.InRange(result.OffsetSeconds, -delay - 0.15, -delay + 0.15);
            Assert.InRange(result.ScaleFactor, 0.998, 1.002);
            Assert.True(SubtitleFileCache.IsValidSrt(result.Path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static double ParseTime(string value) => TimeSpan.ParseExact(value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture).TotalSeconds;
    private static string FormatTime(double value) => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { if (predicate()) return; }
            catch (MpvException) { }
            await Task.Delay(25, timeout.Token);
        }
    }

    [GeneratedRegex(@"\d{2}:\d{2}:\d{2},\d{3}")]
    private static partial Regex TimestampPattern();
}

internal sealed class SubtitleToolsFactAttribute : FactAttribute
{
    public SubtitleToolsFactAttribute()
    {
        if (!SubtitleToolsAvailable()) Skip = "Run tools/Build-SubtitleTools.ps1 to enable real speech integration tests.";
    }

    internal static bool SubtitleToolsAvailable() => File.Exists(Path.Combine(
        Path.GetDirectoryName(typeof(SubtitleSynchronizationIntegrationTests).Assembly.Location)!, "SubtitleTools", "ffsubsync.exe"));
}

internal sealed class SubtitleToolsTheoryAttribute : TheoryAttribute
{
    public SubtitleToolsTheoryAttribute()
    {
        if (!SubtitleToolsFactAttribute.SubtitleToolsAvailable()) Skip = "Run tools/Build-SubtitleTools.ps1 to enable real speech integration tests.";
    }
}
