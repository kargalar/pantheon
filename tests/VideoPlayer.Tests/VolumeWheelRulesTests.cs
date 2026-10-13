using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class VolumeWheelRulesTests
{
    [Theory]
    [InlineData(100, 1, 105)]
    [InlineData(100, -1, 95)]
    [InlineData(149, 1, 150)]
    [InlineData(2, -1, 0)]
    [InlineData(100, 0, 100)]
    public void AdjustChangesFivePercentPerWheelStepAndClamps(int current, double delta, int expected) =>
        Assert.Equal(expected, VolumeWheelRules.Adjust(current, delta));
}
