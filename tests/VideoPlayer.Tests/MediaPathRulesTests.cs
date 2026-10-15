using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MediaPathRulesTests
{
    [Fact]
    public void ResolveExistingVideoPaths_FiltersMissingAndUnsupportedFilesWithoutChangingOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Pantheon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.mkv");
        var secondPath = Path.Combine(directory, "second.mp4");
        File.WriteAllText(firstPath, string.Empty);
        File.WriteAllText(secondPath, string.Empty);

        try
        {
            var paths = MediaPathRules.ResolveExistingVideoPaths(
            [firstPath, Path.Combine(directory, "notes.txt"), secondPath, firstPath.ToUpperInvariant(), null]);

            Assert.Equal([firstPath, secondPath], paths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void MediaDropRules_OnlyAcceptSupportedMediaForTheCurrentPlayerState(
        bool containsVideo, bool containsSubtitle, bool hasActiveVideo, bool expected) =>
        Assert.Equal(expected, MediaDropRules.CanAccept(containsVideo, containsSubtitle, hasActiveVideo));

    [Theory]
    [InlineData("film.mkv")]
    [InlineData("film.MP4")]
    [InlineData("film.webm")]
    [InlineData("film.mpeg")]
    [InlineData("film.m2ts")]
    [InlineData("film.MTS")]
    [InlineData("film.vob")]
    [InlineData("film.3gp")]
    public void IsSupportedVideo_AcceptsKnownVideoFormats(string path) =>
        Assert.True(MediaPathRules.IsSupportedVideo(path));

    [Fact]
    public void VideoFilePickerPatterns_MatchTheSupportedVideoExtensions()
    {
        Assert.NotEmpty(MediaPathRules.VideoFilePickerPatterns);
        Assert.All(MediaPathRules.VideoFilePickerPatterns, pattern =>
            Assert.True(MediaPathRules.IsSupportedVideo($"sample{pattern[1..]}"), pattern));
    }

    [Theory]
    [InlineData("film.srt")]
    [InlineData("film.ASS")]
    [InlineData("film.vtt")]
    [InlineData("film.idx")]
    public void IsSupportedSubtitle_AcceptsExternalSubtitleFormats(string path) =>
        Assert.True(MediaPathRules.IsSupportedSubtitle(path));

    [Fact]
    public void FindMatchingSubtitle_MatchesExactVideoBaseNameAndPrefersSrt()
    {
        var candidates = new[]
        {
            "C:\\Movies\\Episode.en.srt",
            "C:\\Movies\\Episode.ass",
            "C:\\Movies\\episode.SRT",
            "C:\\Movies\\Other.srt"
        };

        Assert.Equal("C:\\Movies\\episode.SRT", MediaPathRules.FindMatchingSubtitle("C:\\Movies\\Episode.mkv", candidates));
    }

    [Fact]
    public void FindMatchingSubtitle_ReturnsNullWhenOnlySimilarNamesExist()
    {
        var candidates = new[] { "C:\\Movies\\Episode.en.srt", "C:\\Movies\\Other.srt" };

        Assert.Null(MediaPathRules.FindMatchingSubtitle("C:\\Movies\\Episode.mkv", candidates));
    }

    [Fact]
    public void FindMatchingSubtitleBesideVideo_UsesPriorityWithoutScanningFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Pantheon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var videoPath = Path.Combine(directory, "Episode.mkv");
            var assPath = Path.Combine(directory, "Episode.ass");
            var srtPath = Path.Combine(directory, "Episode.srt");
            File.WriteAllText(assPath, string.Empty);
            File.WriteAllText(srtPath, string.Empty);

            Assert.Equal(srtPath, MediaPathRules.FindMatchingSubtitleBesideVideo(videoPath));

            File.Delete(srtPath);
            Assert.Equal(assPath, MediaPathRules.FindMatchingSubtitleBesideVideo(videoPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindMatchingSubtitleBesideVideo_ReturnsNullWhenNoExactSiblingExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Pantheon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Episode.en.srt"), string.Empty);

            Assert.Null(MediaPathRules.FindMatchingSubtitleBesideVideo(Path.Combine(directory, "Episode.mkv")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OrderVideosByFileName_SortsSupportedVideoPathsCaseInsensitively()
    {
        var paths = new[]
        {
            "C:\\Movies\\zeta.mkv",
            "C:\\Movies\\beta.mp4",
            "C:\\Movies\\Alpha.avi",
            "C:\\Movies\\notes.txt"
        };

        Assert.Equal(
            new[] { "C:\\Movies\\Alpha.avi", "C:\\Movies\\beta.mp4", "C:\\Movies\\zeta.mkv" },
            MediaPathRules.OrderVideosByFileName(paths));
    }

    [Theory]
    [InlineData("film.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSupportedVideo_RejectsUnsupportedOrMissingPath(string? path) =>
        Assert.False(MediaPathRules.IsSupportedVideo(path));

    [Fact]
    public void CreateCapturePath_UsesVideoNamedSubdirectoryAndTimestampedPng()
    {
        var timestamp = new DateTimeOffset(2026, 9, 29, 14, 5, 6, 123, TimeSpan.Zero);

        var result = MediaPathRules.CreateCapturePath("C:\\Movies\\Example.mkv", "C:\\Users\\Test\\Pictures\\Luma Player", timestamp);

        Assert.Equal(Path.Combine("C:\\Users\\Test\\Pictures\\Luma Player", "Example", "Example-20260929-140506-123.png"), result);
    }

    [Fact]
    public void CreateCapturePath_UsesJpegExtensionForOptimizedScreenshots()
    {
        var timestamp = new DateTimeOffset(2026, 9, 29, 14, 5, 6, 123, TimeSpan.Zero);

        var result = MediaPathRules.CreateCapturePath("C:\\Movies\\Example.mkv", "C:\\Captures", timestamp,
            ScreenshotSaveModeRules.Optimized);

        Assert.Equal(Path.Combine("C:\\Captures", "Example", "Example-20260929-140506-123.jpg"), result);
    }

    [Fact]
    public void CreateCapturePath_RejectsEmptyInput()
    {
        Assert.Throws<ArgumentException>(() => MediaPathRules.CreateCapturePath(" ", "C:\\Pictures", DateTimeOffset.UtcNow));
    }
}


