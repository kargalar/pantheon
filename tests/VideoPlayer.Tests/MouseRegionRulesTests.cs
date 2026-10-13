using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MouseRegionRulesTests
{
    [Theory]
    [InlineData(0.10, 2, 0)]
    [InlineData(0.50, 2, 1)]
    [InlineData(0.10, 3, 0)]
    [InlineData(0.50, 3, 1)]
    [InlineData(0.99, 3, 2)]
    [InlineData(1.00, 3, 2)]
    public void GetRegionIndex_SelectsTheExpectedZone(double position, int count, int expected) =>
        Assert.Equal(expected, MouseRegionRules.GetRegionIndex(position, count));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void GetRegionIndex_RejectsUnsupportedRegionCounts(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MouseRegionRules.GetRegionIndex(0.5, count));
}
