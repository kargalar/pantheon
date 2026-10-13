using System.Diagnostics;
using System.Security.Cryptography;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MpvPlaybackIntegrationTests
{
    private static readonly TimeSpan PropertyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PlaybackCompletion_RaisesAnEndOfFileEvent()
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multitrack.mkv");
        using var player = new MpvContext();
        var endFile = new TaskCompletionSource<MpvEndFileEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        InitializePlayer(player);
        player.EndFile += (_, eventArgs) => endFile.TrySetResult(eventArgs);
        player.LoadFile(mediaPath).Invoke();

        var eventArgs = await endFile.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(MpvEndFileReason.EndOfFile, eventArgs.Reason);
    }

    [Fact]
    public async Task ChapterListExposesTitlesAndSelectingAChapterSeeksToItsStart()
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "chapters.mkv");
        const ulong chapterObservationId = 73;
        using var player = new MpvContext();
        var chapterChangeCount = 0;

        InitializePlayer(player);
        player.PropertyChanged += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.Name, "chapter", StringComparison.Ordinal))
                Interlocked.Increment(ref chapterChangeCount);
        };
        player.ObserveProperty(chapterObservationId, "chapter", MpvFormat.Int64);
        player.LoadFile(mediaPath).Invoke();
        await WaitUntilAsync(() => player.GetProperty<double>("duration") >= 3.9);
        await WaitUntilAsync(() => player.GetProperty<int>("chapter-list/count") == 2);

        Assert.Equal("Introduction", player.GetProperty<string>("chapter-list/0/title"));
        Assert.Equal("Main section", player.GetProperty<string>("chapter-list/1/title"));
        Assert.Equal(2.0, player.GetProperty<double>("chapter-list/1/time"), precision: 2);

        player.SetProperty("pause", true);
        var eventCountBeforeSeek = Volatile.Read(ref chapterChangeCount);
        player.SetProperty("chapter", 1L);
        await WaitUntilAsync(() => Volatile.Read(ref chapterChangeCount) > eventCountBeforeSeek);
        await WaitUntilAsync(() => player.GetProperty<int>("chapter") == 1);
        await WaitUntilAsync(() => Math.Abs(player.GetProperty<double>("time-pos") - 2.0) < 0.15);

        Assert.InRange(player.GetProperty<double>("time-pos"), 1.85, 2.15);
        player.UnobserveProperty(chapterObservationId);
    }

    [Fact]
    public async Task VideoDisplayProperties_AcceptAspectAndScalingChangesDuringPlayback()
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multitrack.mkv");
        using var player = new MpvContext();

        InitializePlayer(player);
        player.LoadFile(mediaPath).Invoke();
        await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);

        player.SetProperty("video-aspect-override", "4:3");
        player.SetProperty("panscan", 1.0);
        player.SetProperty("keepaspect", false);
        await Task.Delay(100);

        Assert.Equal(4.0 / 3.0, double.Parse(player.GetProperty<string>("video-aspect-override")), precision: 5);
        Assert.Equal(1.0, player.GetProperty<double>("panscan"));
        Assert.False(player.GetProperty<bool>("keepaspect"));
    }

    [Fact]
    public async Task ExternalSubtitleAddedDuringMediaSwitch_BelongsToTheNewMedia()
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multitrack.mkv");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pantheon-media-switch-{Guid.NewGuid():N}");
        var secondMediaPath = Path.Combine(tempDirectory, "second.mkv");
        var firstSubtitlePath = Path.Combine(tempDirectory, "first.srt");
        var externalSubtitlePath = Path.Combine(tempDirectory, "second.srt");
        Directory.CreateDirectory(tempDirectory);
        File.Copy(mediaPath, secondMediaPath);
        await File.WriteAllTextAsync(firstSubtitlePath,
            "1\n00:00:00,000 --> 00:00:03,000\nFirst media subtitle\n");
        await File.WriteAllTextAsync(externalSubtitlePath,
            "1\n00:00:00,000 --> 00:00:03,000\nSecond media subtitle\n");
        var player = new MpvContext();

        try
        {
            InitializePlayer(player);
            player.LoadFile(mediaPath).Invoke();
            await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
            player.SubAdd(firstSubtitlePath, LoadOption.Select).Invoke();
            await WaitUntilAsync(() => HasExternalSubtitle(player, firstSubtitlePath));
            player.SetProperty("pause", true);

            player.LoadFile(secondMediaPath).Invoke();
            player.SubAdd(externalSubtitlePath, LoadOption.Select).Invoke();

            await WaitUntilAsync(() => string.Equals(
                player.GetProperty<string>("path"), secondMediaPath, StringComparison.OrdinalIgnoreCase));
            await WaitUntilAsync(() => HasExternalSubtitle(player, externalSubtitlePath));
            Assert.False(HasExternalSubtitle(player, firstSubtitlePath));
        }
        finally
        {
            player.Dispose();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1254)]
    [InlineData(65001)]
    public async Task ExternalSubtitle_DecodesTurkishTextWithSelectedCodepage(int encodingCodepage)
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multitrack.mkv");
        var subtitlePath = Path.Combine(Path.GetTempPath(), $"pantheon-turkish-subtitle-{Guid.NewGuid():N}.srt");
        const string expectedText = "\u0130\u011F\u00FC\u015F\u00F6\u00E7 \u00C7\u011E\u0130\u00D6\u015E\u00DC";
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var subtitleBytes = System.Text.Encoding.GetEncoding(encodingCodepage).GetBytes(
            $"1\n00:00:00,000 --> 00:00:03,000\n{expectedText}\n");
        await File.WriteAllBytesAsync(subtitlePath, subtitleBytes);
        using var player = new MpvContext();

        try
        {
            InitializePlayer(player);
            player.LoadFile(mediaPath).Invoke();
            await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
            player.SetProperty("pause", true);
            player.SetPropertyString("sub-codepage", "auto");
            player.SubAdd(subtitlePath, LoadOption.Select).Invoke();
            await WaitUntilAsync(() => HasExternalSubtitle(player, subtitlePath));
            player.Seek(1.0, SeekOption.Absolute | SeekOption.Exact).Invoke();
            await WaitUntilAsync(() => !player.GetProperty<bool>("seeking"));
            var actualText = string.Empty;
            await WaitUntilAsync(() =>
            {
                actualText = player.GetProperty<string>("sub-text");
                return !string.IsNullOrWhiteSpace(actualText);
            });

            var trackId = GetExternalSubtitleId(player, subtitlePath);
            player.SetPropertyString("sub-codepage", "cp1254");
            player.SubReload(trackId).Invoke();
            player.Seek(1.0, SeekOption.Absolute | SeekOption.Exact).Invoke();
            await WaitUntilAsync(() => !player.GetProperty<bool>("seeking"));
            await WaitUntilAsync(() =>
            {
                actualText = player.GetProperty<string>("sub-text") ?? string.Empty;
                return actualText.Contains(expectedText, StringComparison.Ordinal);
            });

            Assert.Contains(expectedText, actualText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(subtitlePath)) File.Delete(subtitlePath);
        }
    }

    [Fact]
    public async Task MultitrackMedia_SupportsTrackSelectionSeekingAndScreenshotCapture()
    {
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multitrack.mkv");
        var capturePath = Path.Combine(Path.GetTempPath(), $"pantheon-integration-{Guid.NewGuid():N}.png");
        var subtitleCapturePath = Path.Combine(Path.GetTempPath(), $"pantheon-integration-subtitles-{Guid.NewGuid():N}.png");
        var externalSubtitlePath = Path.Combine(Path.GetTempPath(), $"pantheon-subtitle-{Guid.NewGuid():N}.srt");
        const ulong trackListObservationId = 42;
        using var player = new MpvContext();
        var trackListEventCount = 0;
        var isTrackListObserved = false;

        try
        {
            InitializePlayer(player);
            player.PropertyChanged += (_, eventArgs) =>
            {
                if (string.Equals(eventArgs.Name, "track-list", StringComparison.Ordinal))
                    Interlocked.Increment(ref trackListEventCount);
            };
            player.ObserveProperty(trackListObservationId, "track-list", MpvFormat.Node);
            isTrackListObserved = true;
            player.LoadFile(mediaPath).Invoke();

            await WaitUntilAsync(() => player.GetProperty<double>("duration") > 0);
            Assert.Equal(Path.GetFullPath(mediaPath), Path.GetFullPath(player.GetProperty<string>("path")));

            await WaitUntilAsync(() => player.GetProperty<int>("track-list/count") >= 5);
            await WaitUntilAsync(() => Volatile.Read(ref trackListEventCount) > 0);
            var trackCount = player.GetProperty<int>("track-list/count");
            var audioTracks = GetTrackIndexes(player, trackCount, "audio");
            var subtitleTracks = GetTrackIndexes(player, trackCount, "sub");
            Assert.Equal(2, audioTracks.Count);
            Assert.Equal(2, subtitleTracks.Count);

            var selectedAudioId = player.GetProperty<int>($"track-list/{audioTracks[1]}/id");
            var eventsBeforeAudioSelection = Volatile.Read(ref trackListEventCount);
            player.SetProperty("aid", (long)selectedAudioId);
            await WaitUntilAsync(() => Volatile.Read(ref trackListEventCount) > eventsBeforeAudioSelection);
            await WaitUntilAsync(() => player.GetProperty<bool>($"track-list/{audioTracks[1]}/selected")
                && !player.GetProperty<bool>($"track-list/{audioTracks[0]}/selected"));

            var selectedSubtitleId = player.GetProperty<int>($"track-list/{subtitleTracks[1]}/id");
            var eventsBeforeSubtitleSelection = Volatile.Read(ref trackListEventCount);
            player.SetProperty("sid", (long)selectedSubtitleId);
            await WaitUntilAsync(() => Volatile.Read(ref trackListEventCount) > eventsBeforeSubtitleSelection);
            await WaitUntilAsync(() => player.GetProperty<bool>($"track-list/{subtitleTracks[1]}/selected")
                && !player.GetProperty<bool>($"track-list/{subtitleTracks[0]}/selected"));

            await File.WriteAllTextAsync(externalSubtitlePath, "1\n00:00:00,000 --> 00:00:03,000\nExternal subtitle\n");
            var eventsBeforeExternalTrack = Volatile.Read(ref trackListEventCount);
            player.SubAdd(externalSubtitlePath, LoadOption.Select).Invoke();
            await WaitUntilAsync(() => player.GetProperty<int>("track-list/count") == trackCount + 1);
            await WaitUntilAsync(() => Volatile.Read(ref trackListEventCount) > eventsBeforeExternalTrack);
            var externalTrackIndex = trackCount;
            Assert.Equal(externalSubtitlePath, player.GetProperty<string>($"track-list/{externalTrackIndex}/external-filename"));
            Assert.True(player.GetProperty<bool>($"track-list/{externalTrackIndex}/selected"));
            var externalTrackId = player.GetProperty<int>($"track-list/{externalTrackIndex}/id");
            player.SetPropertyString("sid", "no");
            await WaitUntilAsync(() => !player.GetProperty<bool>($"track-list/{externalTrackIndex}/selected"));
            player.SetProperty("sid", (long)externalTrackId);
            await WaitUntilAsync(() => player.GetProperty<bool>($"track-list/{externalTrackIndex}/selected"));
            player.SetProperty("sub-delay", 0.5);
            Assert.Equal(0.5, player.GetProperty<double>("sub-delay"));

            player.SetProperty("pause", true);
            foreach (var targetPosition in new[] { 0.3, 2.0, 3.5 })
                await SeekWithinPreviewDeadlineAsync(player, targetPosition);

            await SeekWithinPreviewDeadlineAsync(player, 1.0);
            player.ScreenshotToFile(capturePath, ScreenshotOptions.Video).Invoke();
            player.ScreenshotToFile(subtitleCapturePath, ScreenshotOptions.Subtitles).Invoke();
            Assert.True(File.Exists(capturePath));
            Assert.True(new FileInfo(capturePath).Length > 0);
            Assert.True(File.Exists(subtitleCapturePath));
            Assert.NotEqual(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(capturePath))),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(subtitleCapturePath))));
        }
        finally
        {
            if (isTrackListObserved) player.UnobserveProperty(trackListObservationId);
            if (File.Exists(capturePath)) File.Delete(capturePath);
            if (File.Exists(subtitleCapturePath)) File.Delete(subtitleCapturePath);
            if (File.Exists(externalSubtitlePath)) File.Delete(externalSubtitlePath);
        }
    }

    private static void InitializePlayer(MpvContext player)
    {
        player.Initialize();
        // Decode audio without requiring a physical output device on CI runners.
        player.SetPropertyString("ao", "null");
    }

    private static List<int> GetTrackIndexes(MpvContext player, int trackCount, string type)
    {
        var indexes = new List<int>();
        for (var index = 0; index < trackCount; index++)
            if (string.Equals(player.GetProperty<string>($"track-list/{index}/type"), type, StringComparison.Ordinal))
                indexes.Add(index);
        return indexes;
    }

    private static bool HasExternalSubtitle(MpvContext player, string subtitlePath)
    {
        var count = Math.Clamp(player.GetProperty<int>("track-list/count"), 0, 256);
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(player.GetProperty<string>($"track-list/{index}/type"), "sub", StringComparison.Ordinal))
                continue;
            if (string.Equals(player.GetProperty<string>($"track-list/{index}/external-filename"),
                subtitlePath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static int GetExternalSubtitleId(MpvContext player, string subtitlePath)
    {
        var count = Math.Clamp(player.GetProperty<int>("track-list/count"), 0, 256);
        for (var index = 0; index < count; index++)
        {
            if (string.Equals(player.GetProperty<string>($"track-list/{index}/type"), "sub", StringComparison.Ordinal)
                && string.Equals(player.GetProperty<string>($"track-list/{index}/external-filename"),
                    subtitlePath, StringComparison.OrdinalIgnoreCase))
                return player.GetProperty<int>($"track-list/{index}/id");
        }
        throw new InvalidOperationException($"External subtitle track was not found: {subtitlePath}");
    }

    private static async Task SeekWithinPreviewDeadlineAsync(MpvContext player, double targetPositionSeconds)
    {
        var timer = Stopwatch.StartNew();
        player.Seek(targetPositionSeconds, SeekOption.Absolute | SeekOption.Exact).Invoke();
        await WaitUntilAsync(() => PlaybackSeekRules.HasReachedTarget(
            player.GetProperty<bool>("seeking"), player.GetProperty<double>("time-pos"), targetPositionSeconds));
        timer.Stop();

        Assert.True(timer.Elapsed <= TimeSpan.FromSeconds(2),
            $"Seek to {targetPositionSeconds:F2}s took {timer.Elapsed.TotalMilliseconds:F0}ms, "
            + "exceeding the timeline preview's 2-second deadline.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(PropertyTimeout);
        while (true)
        {
            try
            {
                if (condition()) return;
            }
            catch (MpvException)
            {
                // Some properties are temporarily unavailable while a file is opening or seeking.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
