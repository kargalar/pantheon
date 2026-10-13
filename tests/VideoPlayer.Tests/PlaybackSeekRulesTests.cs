using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class PlaybackSeekRulesTests
{
    [Theory]
    [InlineData(false, 10, 10, 10, true)]
    [InlineData(false, 10.25, 10, 10, true)]
    [InlineData(false, 10.2501, 20, 10, false)]
    [InlineData(false, 10.5, 0, 10, true)]
    [InlineData(false, 9.5, 20, 10, true)]
    [InlineData(true, 10, 0, 10, false)]
    public void HasReachedOrPassedTarget_RequiresCompletedSeekInTargetDirection(
        bool isSeeking,
        double currentPositionSeconds,
        double startPositionSeconds,
        double targetPositionSeconds,
        bool expected)
    {
        Assert.Equal(expected, PlaybackSeekRules.HasReachedOrPassedTarget(
            isSeeking, currentPositionSeconds, startPositionSeconds, targetPositionSeconds));
    }

    [Fact]
    public void HasReachedOrPassedTarget_RejectsNonFinitePositions()
    {
        Assert.False(PlaybackSeekRules.HasReachedOrPassedTarget(false, double.NaN, 0, 10));
        Assert.False(PlaybackSeekRules.HasReachedOrPassedTarget(false, 10, 0, double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0.0, 0.0, 120.0, true, 120.0)]
    [InlineData(0.0, 0.0, 120.0, false, 120.0)]
    [InlineData(120.5, 0.0, 120.0, false, 120.5)]
    [InlineData(42.0, null, null, false, 42.0)]
    public void GetPositionForPersistence_PreservesResumeTargetUntilSeekCompletes(
        double currentPositionSeconds,
        double? startPositionSeconds,
        double? pendingResumePositionSeconds,
        bool isSeeking,
        double expectedPositionSeconds)
    {
        Assert.Equal(expectedPositionSeconds, PlaybackSeekRules.GetPositionForPersistence(
            currentPositionSeconds, startPositionSeconds, pendingResumePositionSeconds, isSeeking));
    }

    [Fact]
    public void InFlightResumeSeek_PersistsTargetAndCurrentSubtitleDelay()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var currentPositionSeconds = 0.0;
            var savedPositionSeconds = PlaybackSeekRules.GetPositionForPersistence(
                currentPositionSeconds,
                startPositionSeconds: 0,
                pendingResumePositionSeconds: 120,
                isSeeking: true);
            var store = new PlaybackProgressStore(storePath);
            store.Save(videoPath, new PlaybackProgress(savedPositionSeconds, -0.25));

            var loaded = new PlaybackProgressStore(storePath).Get(videoPath);

            Assert.Equal(120, loaded.PositionSeconds);
            Assert.Equal(-0.25, loaded.SubtitleDelaySeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
