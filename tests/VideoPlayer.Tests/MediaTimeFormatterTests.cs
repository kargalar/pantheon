using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MediaTimeFormatterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.99, 0)]
    [InlineData(1, 1)]
    [InlineData(1.99, 1)]
    [InlineData(2, 2)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void GetPlaybackSecond_ReturnsTheDisplayedWholeSecond(double seconds, long expected) =>
        Assert.Equal(expected, MediaTimeFormatter.GetPlaybackSecond(seconds));

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(86399, "23:59:59")]
    [InlineData(86401, "24:00:01")]
    [InlineData(360000, "100:00:00")]
    [InlineData(-1, "00:00:00")]
    [InlineData(double.NaN, "00:00:00")]
    [InlineData(double.PositiveInfinity, "00:00:00")]
    public void FormatPlaybackTime_DoesNotWrapAtTwentyFourHours(double seconds, string expected) =>
        Assert.Equal(expected, MediaTimeFormatter.FormatPlaybackTime(seconds));

    [Theory]
    [InlineData(59, "0:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(90000, "25:00:00")]
    public void FormatPreviewTime_UsesHoursForLongVideosWithoutWrapping(double seconds, string expected) =>
        Assert.Equal(expected, MediaTimeFormatter.FormatPreviewTime(seconds));

    [Theory]
    [InlineData(600, 1.2, "00:08:20")]
    [InlineData(600, 2.0, "00:05:00")]
    [InlineData(600, 0.5, "00:20:00")]
    [InlineData(600, 1.0, "00:10:00")]
    [InlineData(120, 1.2, "00:01:40")]
    [InlineData(600, 0, "00:10:00")]
    [InlineData(600, -1, "00:10:00")]
    [InlineData(600, double.NaN, "00:10:00")]
    public void FormatPlaybackTime_ScalesWithPlaybackSpeed(double seconds, double speed, string expected) =>
        Assert.Equal(expected, MediaTimeFormatter.FormatPlaybackTime(seconds, speed));

    [Theory]
    [InlineData(12, 1.2, 10)]
    [InlineData(600, 1.2, 500)]
    [InlineData(600, 2.0, 300)]
    public void GetPlaybackSecond_ScalesWithPlaybackSpeed(double seconds, double speed, long expected) =>
        Assert.Equal(expected, MediaTimeFormatter.GetPlaybackSecond(seconds, speed));
}
