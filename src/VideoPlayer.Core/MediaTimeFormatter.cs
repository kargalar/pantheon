namespace VideoPlayer.Core;

public static class MediaTimeFormatter
{
    public static long GetPlaybackSecond(double seconds) => (long)Math.Floor(ToTimeSpan(seconds).TotalSeconds);

    public static string FormatPlaybackTime(double seconds)
    {
        var time = ToTimeSpan(seconds);
        var totalHours = (long)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    public static string FormatPreviewTime(double seconds)
    {
        var time = ToTimeSpan(seconds);
        if (time.TotalHours < 1)
            return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        return FormatPlaybackTime(seconds);
    }

    private static TimeSpan ToTimeSpan(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return TimeSpan.Zero;
        var safeSeconds = Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1);
        return TimeSpan.FromSeconds(safeSeconds);
    }
}
