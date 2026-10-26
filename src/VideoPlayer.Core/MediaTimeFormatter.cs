namespace VideoPlayer.Core;

public static class MediaTimeFormatter
{
    public static long GetPlaybackSecond(double seconds, double playbackSpeed = 1.0) =>
        (long)Math.Floor(ToTimeSpan(ScaleBySpeed(seconds, playbackSpeed)).TotalSeconds);

    public static string FormatPlaybackTime(double seconds, double playbackSpeed = 1.0)
    {
        var time = ToTimeSpan(ScaleBySpeed(seconds, playbackSpeed));
        var totalHours = (long)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    public static string FormatPreviewTime(double seconds, double playbackSpeed = 1.0)
    {
        var time = ToTimeSpan(ScaleBySpeed(seconds, playbackSpeed));
        if (time.TotalHours < 1)
            return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        return FormatPlaybackTime(seconds, playbackSpeed);
    }

    public static double ScaleBySpeed(double seconds, double playbackSpeed)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        if (!double.IsFinite(playbackSpeed) || playbackSpeed <= 0) return seconds;
        return seconds / playbackSpeed;
    }

    private static TimeSpan ToTimeSpan(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return TimeSpan.Zero;
        var safeSeconds = Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1);
        return TimeSpan.FromSeconds(safeSeconds);
    }
}
