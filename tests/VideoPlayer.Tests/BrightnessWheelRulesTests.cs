using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class BrightnessWheelRulesTests
{
    [Theory]
    [InlineData(50, 1, 55)]
    [InlineData(50, -1, 45)]
    [InlineData(98, 1, 100)]
    [InlineData(3, -1, 0)]
    [InlineData(50, 0, 50)]
    [InlineData(0, -1, 0)]
    [InlineData(100, 1, 100)]
    public void AdjustChangesBrightnessByStepAndClamps(int current, double delta, int expected) =>
        Assert.Equal(expected, BrightnessWheelRules.Adjust(current, delta));
}

