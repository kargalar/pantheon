using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class ChapterNavigationRulesTests
{
    [Theory]
    [InlineData(2, 4, -1, 1)]
    [InlineData(2, 4, 1, 3)]
    [InlineData(-1, 4, 1, 0)]
    [InlineData(0, 4, -1, null)]
    [InlineData(3, 4, 1, null)]
    [InlineData(-1, 0, 1, null)]
    public void GetAdjacentChapterIndex_RespectsCurrentPositionAndBounds(
        int currentIndex, int chapterCount, int direction, int? expectedIndex) =>
        Assert.Equal(expectedIndex, ChapterNavigationRules.GetAdjacentChapterIndex(currentIndex, chapterCount, direction));

    [Fact]
    public void GetAdjacentChapterIndex_RejectsDirectionsOtherThanPreviousOrNext() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChapterNavigationRules.GetAdjacentChapterIndex(0, 2, 0));
}
