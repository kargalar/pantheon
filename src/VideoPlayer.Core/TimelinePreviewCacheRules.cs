namespace VideoPlayer.Core;

public static class TimelinePreviewCacheRules
{
    public static IReadOnlyList<long> GetIndexesOutsideRadius(IEnumerable<long> indexes, long centerIndex, int radius)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        return indexes.Where(index => Math.Abs(index - centerIndex) > radius).ToArray();
    }
}
