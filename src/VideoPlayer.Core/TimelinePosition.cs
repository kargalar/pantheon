namespace VideoPlayer.Core;

public static class TimelinePosition
{
    public const double ThumbInset = 8;

    public static double PercentFromPointer(double x, double width)
    {
        if (!double.IsFinite(x) || !double.IsFinite(width) || width <= ThumbInset * 2) return 0;
        return Math.Clamp((x - ThumbInset) / (width - ThumbInset * 2) * 100, 0, 100);
    }

    public static double SecondsFromPercent(double percent, double duration)
    {
        if (!double.IsFinite(percent) || !double.IsFinite(duration) || duration <= 0) return 0;
        return Math.Min(Math.Clamp(percent, 0, 100) / 100 * duration, Math.Max(0, duration - 0.001));
    }
}
