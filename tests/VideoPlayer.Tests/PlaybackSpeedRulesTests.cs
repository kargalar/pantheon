using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class PlaybackSpeedRulesTests
{
    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(1.0, 1.0)]
    [InlineData(7.25, 7.25)]
    [InlineData(10.0, 10.0)]
    [InlineData(12.0, 10.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void ClampKeepsPlaybackSpeedBetweenQuarterAndTen(double input, double expected) =>
        Assert.Equal(expected, PlaybackSpeedRules.Clamp(input));
}
