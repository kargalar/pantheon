using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class TimelinePreviewCacheRulesTests
{
    [Fact]
    public void GetIndexesOutsideRadius_KeepsBoundaryAndNearbyIndexes()
    {
        var expired = TimelinePreviewCacheRules.GetIndexesOutsideRadius([1, 2, 3, 5, 7, 8, 9], 5, 2);

        Assert.Equal(new long[] { 1, 2, 8, 9 }, expired);
    }
}
