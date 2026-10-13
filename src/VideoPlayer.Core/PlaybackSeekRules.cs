namespace VideoPlayer.Core;

public static class PlaybackSeekRules
{
    public const double TargetToleranceSeconds = 0.25;

    public static bool HasReachedTarget(bool isSeeking, double currentPositionSeconds, double targetPositionSeconds) =>
        !isSeeking
        && double.IsFinite(currentPositionSeconds)
        && double.IsFinite(targetPositionSeconds)
        && Math.Abs(currentPositionSeconds - targetPositionSeconds) <= TargetToleranceSeconds;

    public static bool HasReachedOrPassedTarget(
        bool isSeeking,
        double currentPositionSeconds,
        double startPositionSeconds,
        double targetPositionSeconds)
    {
        if (isSeeking || !double.IsFinite(startPositionSeconds)) return false;
        if (HasReachedTarget(isSeeking, currentPositionSeconds, targetPositionSeconds)) return true;

        if (!double.IsFinite(currentPositionSeconds) || !double.IsFinite(targetPositionSeconds)) return false;

        return targetPositionSeconds >= startPositionSeconds
            ? currentPositionSeconds > targetPositionSeconds
            : currentPositionSeconds < targetPositionSeconds;
    }

    public static double GetPositionForPersistence(
        double currentPositionSeconds,
        double? startPositionSeconds,
        double? pendingResumePositionSeconds,
        bool isSeeking)
    {
        if (pendingResumePositionSeconds is not { } targetPositionSeconds
            || startPositionSeconds is not { } startPosition
            || HasReachedOrPassedTarget(isSeeking, currentPositionSeconds, startPosition, targetPositionSeconds))
            return currentPositionSeconds;

        return targetPositionSeconds;
    }
}
