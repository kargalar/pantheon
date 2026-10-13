using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class TimelinePositionTests
{
    [Theory]
    [InlineData(8, 1016, 0)]
    [InlineData(508, 1016, 50)]
    [InlineData(1008, 1016, 100)]
    [InlineData(-100, 1016, 0)]
    [InlineData(2000, 1016, 100)]
    [InlineData(50, 0, 0)]
    [InlineData(double.NaN, 1016, 0)]
    public void PointerMapsToTrackAndClampsOutside(double x, double width, double expected) =>
        Assert.Equal(expected, TimelinePosition.PercentFromPointer(x, width));

    [Theory]
    [InlineData(50, 600, 300)]
    [InlineData(-10, 600, 0)]
    [InlineData(100, 600, 599.999)]
    [InlineData(150, 600, 599.999)]
    [InlineData(50, 0, 0)]
    [InlineData(50, double.NaN, 0)]
    public void SeekTargetStaysInsideVideo(double percent, double duration, double expected) =>
        Assert.Equal(expected, TimelinePosition.SecondsFromPercent(percent, duration), 6);
}
