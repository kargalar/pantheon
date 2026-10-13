namespace VideoPlayer.Core;

public static class MouseRegionRules
{
    public static int GetRegionIndex(double relativePosition, int regionCount)
    {
        if (regionCount is not (2 or 3))
            throw new ArgumentOutOfRangeException(nameof(regionCount), "Mouse zones support two or three regions.");
        if (!double.IsFinite(relativePosition))
            throw new ArgumentOutOfRangeException(nameof(relativePosition));

        var normalizedPosition = Math.Clamp(relativePosition, 0, 1);
        return Math.Min((int)(normalizedPosition * regionCount), regionCount - 1);
    }
}
