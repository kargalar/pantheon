using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class QueueOrderRulesTests
{
    [Fact]
    public void CreatePlaybackSnapshotKeepsCurrentFirstAndPreservesQueuedDuplicatesWithinLimit()
    {
        var snapshot = QueueOrderRules.CreatePlaybackSnapshot(
            "current.mkv", ["next.mkv", "next.mkv", "later.mkv"], currentPathEnded: false, maximumEntryCount: 3);

        Assert.Equal(["current.mkv", "next.mkv", "next.mkv"], snapshot);
        Assert.Empty(QueueOrderRules.CreatePlaybackSnapshot(
            "current.mkv", ["next.mkv"], currentPathEnded: false, maximumEntryCount: 0));
    }

    [Fact]
    public void CreatePlaybackSnapshotOmitsEndedCurrentVideo()
    {
        var snapshot = QueueOrderRules.CreatePlaybackSnapshot(
            "ended.mkv", ["next.mkv"], currentPathEnded: true, maximumEntryCount: 10);

        Assert.Equal(["next.mkv"], snapshot);
    }

    [Fact]
    public void TakeNextAvailablePath_SkipsUnavailableItemsAndRemovesThemFromTheQueue()
    {
        var queue = new List<string> { "removed.mkv", "next.mkv", "later.mkv" };

        var next = QueueOrderRules.TakeNextAvailablePath(queue, path => path == "next.mkv");

        Assert.Equal("next.mkv", next);
        Assert.Equal(["later.mkv"], queue);
    }

    [Fact]
    public void TakeNextAvailablePath_ClearsQueueWhenNoItemCanBeOpened()
    {
        var queue = new List<string> { "removed-one.mkv", "removed-two.mkv" };

        var next = QueueOrderRules.TakeNextAvailablePath(queue, _ => false);

        Assert.Null(next);
        Assert.Empty(queue);
    }

    [Fact]
    public void AppendUniquePathsAddsOnlyNewPathsIgnoringCase()
    {
        var existing = Path.GetFullPath("existing.mp4");
        var addition = Path.GetFullPath("addition.mp4");
        var queue = new List<string> { existing };

        var addedCount = QueueOrderRules.AppendUniquePaths(queue,
        [
            existing.ToUpperInvariant(),
            addition,
            addition.ToUpperInvariant()
        ]);

        Assert.Equal(1, addedCount);
        Assert.Equal([existing, addition], queue);
    }

    [Fact]
    public void AppendUniquePathsNormalizesRelativeQueueEntries()
    {
        var queue = new List<string> { Path.Combine("folder", "..", "existing.mp4") };
        var fullPath = Path.GetFullPath("existing.mp4");

        var addedCount = QueueOrderRules.AppendUniquePaths(queue, [fullPath]);

        Assert.Equal(0, addedCount);
        Assert.Single(queue);
    }

    [Fact]
    public void CombinePlaylistEntriesAndDroppedVideosPreservesPlaylistRepeatsAndAppendsUniqueVideos()
    {
        var playlistEntries = new[] { "first.mp4", "first.mp4", "second.mp4" };

        var combined = QueueOrderRules.CombinePlaylistEntriesAndDroppedVideos(
            playlistEntries,
            ["FIRST.MP4", "third.mp4", "THIRD.MP4", "second.mp4"],
            maximumEntryCount: 10);

        Assert.Equal(
            ["first.mp4", "first.mp4", "second.mp4", Path.GetFullPath("third.mp4")],
            combined);
    }

    [Fact]
    public void CombinePlaylistEntriesAndDroppedVideosUsesDroppedVideosWhenPlaylistIsEmpty()
    {
        var combined = QueueOrderRules.CombinePlaylistEntriesAndDroppedVideos(
            [],
            ["first.mp4", "FIRST.MP4", "second.mp4"],
            maximumEntryCount: 10);

        Assert.Equal([Path.GetFullPath("first.mp4"), Path.GetFullPath("second.mp4")], combined);
    }

    [Fact]
    public void CombinePlaylistEntriesAndDroppedVideosEnforcesCombinedQueueLimit()
    {
        Assert.Throws<InvalidDataException>(() => QueueOrderRules.CombinePlaylistEntriesAndDroppedVideos(
            ["first.mp4", "second.mp4"], ["third.mp4"], maximumEntryCount: 2));
    }

    [Fact]
    public void CombinePlaylistEntriesAndDroppedVideosDoesNotChargeDuplicatesAgainstTheLimit()
    {
        var combined = QueueOrderRules.CombinePlaylistEntriesAndDroppedVideos(
            ["first.mp4", "second.mp4"], ["FIRST.MP4", "third.mp4"], maximumEntryCount: 3);

        Assert.Equal(
            ["first.mp4", "second.mp4", Path.GetFullPath("third.mp4")],
            combined);
    }

    [Theory]
    [InlineData(0, 2, new[] { "b", "c", "a" })]
    [InlineData(2, 0, new[] { "c", "a", "b" })]
    [InlineData(1, 1, new[] { "a", "b", "c" })]
    [InlineData(-1, 2, new[] { "a", "b", "c" })]
    [InlineData(0, 3, new[] { "a", "b", "c" })]
    public void MoveReordersItemsAndLeavesInvalidPositionsAlone(int source, int destination, string[] expected)
    {
        var items = new List<string> { "a", "b", "c" };

        QueueOrderRules.Move(items, source, destination);

        Assert.Equal(expected, items);
    }
}
