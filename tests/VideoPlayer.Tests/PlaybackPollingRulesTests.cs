using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class PlaybackPollingRulesTests
{
    [Theory]
    [InlineData(true, false, 350)]
    [InlineData(false, false, 1500)]
    [InlineData(true, true, 1500)]
    public void GetInterval_SlowsPollingWhenPlaybackIsIdleOrSeeking(bool isPlaying, bool isSeeking, int expectedMilliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), PlaybackPollingRules.GetInterval(isPlaying, isSeeking));
}
