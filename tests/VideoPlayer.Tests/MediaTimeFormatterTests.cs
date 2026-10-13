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
}
