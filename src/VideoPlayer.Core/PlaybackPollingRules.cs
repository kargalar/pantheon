namespace VideoPlayer.Core;

public static class PlaybackPollingRules
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan GetInterval(bool isPlaying, bool isSeeking) => isPlaying && !isSeeking
        ? ActiveInterval
        : IdleInterval;
}
