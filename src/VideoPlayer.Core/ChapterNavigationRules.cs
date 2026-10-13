namespace VideoPlayer.Core;

public static class ChapterNavigationRules
{
    public static int? GetAdjacentChapterIndex(int currentIndex, int chapterCount, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chapterCount);
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "Chapter navigation direction must be -1 or 1.");
        if (chapterCount == 0 || currentIndex < -1 || currentIndex >= chapterCount) return null;

        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < chapterCount ? targetIndex : null;
    }
}
