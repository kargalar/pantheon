using VideoPlayer.Core;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class PlaybackProgressStoreTests
{
    [Theory]
    [InlineData(100, 100.99, 0, 0, false)]
    [InlineData(100, 101, 0, 0, true)]
    [InlineData(100, 100, 0, 0.01, true)]
    public void HasMeaningfulChange_DetectsPositionAndSubtitleDelayUpdates(
        double previousPosition,
        double currentPosition,
        double previousSubtitleDelay,
        double currentSubtitleDelay,
        bool expected)
    {
        var previous = new PlaybackProgress(previousPosition, previousSubtitleDelay);
        var current = new PlaybackProgress(currentPosition, currentSubtitleDelay);

        Assert.Equal(expected, PlaybackProgressRules.HasMeaningfulChange(previous, current));
    }

    [Fact]
    public void HasMeaningfulChange_TreatsFirstProgressAsNew()
    {
        Assert.True(PlaybackProgressRules.HasMeaningfulChange(null, new PlaybackProgress(10)));
    }

    [Fact]
    public void SaveAndLoad_PreservePositionAndSubtitleDelay()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            store.Save(videoPath, new PlaybackProgress(812.5, -0.3));

            var loaded = new PlaybackProgressStore(storePath).Get(videoPath.ToUpperInvariant());

            Assert.Equal(812.5, loaded.PositionSeconds);
            Assert.Equal(-0.3, loaded.SubtitleDelaySeconds);
            Assert.False(File.Exists(storePath + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_PreservePlaybackSpeedSubtitlePathAndDelay()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        var subtitlePath = Path.Combine(directory, "episode.custom.srt");
        try
        {
            var expected = new PlaybackProgress(812.5, -0.3, 1.75, subtitlePath);
            new PlaybackProgressStore(storePath).Save(videoPath, expected);

            var loaded = new PlaybackProgressStore(storePath).Get(videoPath);

            Assert.Equal(expected with { SubtitlePath = Path.GetFullPath(subtitlePath) }, loaded);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_PreserveUnicodeMediaPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "\u0130\u011F\u00FC\u015F\u00F6\u00E7-\uD83D\uDE00.mkv");
        try
        {
            new PlaybackProgressStore(storePath).Save(videoPath, new PlaybackProgress(123.5, 0.25));

            var loaded = new PlaybackProgressStore(storePath).Get(videoPath);

            Assert.Equal(new PlaybackProgress(123.5, 0.25), loaded);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ReadsLargeValidHistoryAndPreservesEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var expectedPath = Path.Combine(directory, "episode-09999.mkv");
        try
        {
            var history = Enumerable.Range(0, 10_000)
                .ToDictionary(
                    index => Path.Combine(directory, $"episode-{index:D5}.mkv"),
                    index => new PlaybackProgress(index + 20));
            File.WriteAllText(storePath, JsonSerializer.Serialize(history));

            var store = new PlaybackProgressStore(storePath);

            Assert.Equal(10_019, store.Get(expectedPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAndSave_KeepHistoryWithinTheConfiguredEntryLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var history = Enumerable.Range(0, PlaybackProgressStore.MaximumEntryCount + 5)
            .ToDictionary(
                index => Path.Combine(directory, $"episode-{index:D5}.mkv"),
                index => new PlaybackProgress(index + 20));
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(history));
            var store = new PlaybackProgressStore(storePath);
            var newVideoPath = Path.Combine(directory, "new-episode.mkv");

            await store.FlushAsync();
            Assert.Equal(PlaybackProgressStore.MaximumEntryCount, CountStoredEntries(storePath));

            store.Save(newVideoPath, new PlaybackProgress(999));

            var reloadedStore = new PlaybackProgressStore(storePath);
            Assert.Equal(new PlaybackProgress(0), reloadedStore.Get(Path.Combine(directory, "episode-00000.mkv")));
            Assert.Equal(new PlaybackProgress(0), reloadedStore.Get(Path.Combine(directory, "episode-00005.mkv")));
            Assert.Equal(26, reloadedStore.Get(Path.Combine(directory, "episode-00006.mkv")).PositionSeconds);
            Assert.Equal(PlaybackProgressStore.MaximumEntryCount, CountStoredEntries(storePath));
            Assert.Equal(999, reloadedStore.Get(newVideoPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_SupportsUtf8Bom()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var history = new Dictionary<string, PlaybackProgress> { [videoPath] = new(312) };
            File.WriteAllText(storePath, JsonSerializer.Serialize(history), new UTF8Encoding(true));

            var store = new PlaybackProgressStore(storePath);

            Assert.Equal(312, store.Get(videoPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReadsHistoryIntoDeferredStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(new Dictionary<string, PlaybackProgress>
            {
                [videoPath] = new(321.5, -0.2)
            }));
            var store = new PlaybackProgressStore(storePath, loadImmediately: false);

            var loadTask = store.LoadAsync();
            Assert.Same(loadTask, store.LoadAsync());
            await loadTask;

            Assert.Equal(new PlaybackProgress(321.5, -0.2), store.Get(videoPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FlushAsync_WaitsForDeferredLoadAndItsHistoryCompaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var history = Enumerable.Range(0, PlaybackProgressStore.MaximumEntryCount + 5)
            .ToDictionary(
                index => Path.Combine(directory, $"episode-{index:D5}.mkv"),
                index => new PlaybackProgress(index + 20));
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(history));
            var store = new PlaybackProgressStore(storePath, loadImmediately: false);
            _ = store.LoadAsync();

            await store.FlushAsync();

            Assert.Equal(PlaybackProgressStore.MaximumEntryCount, CountStoredEntries(storePath));
            Assert.Equal(10_024, new PlaybackProgressStore(storePath).Get(
                Path.Combine(directory, "episode-10004.mkv")).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeferredLoad_ScheduleSaveAndRemoveApplyAfterLoadedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var existingVideoPath = Path.Combine(directory, "existing.mkv");
        var newVideoPath = Path.Combine(directory, "new.mkv");
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(new Dictionary<string, PlaybackProgress>
            {
                [existingVideoPath] = new(321.5, -0.2)
            }));
            var store = new PlaybackProgressStore(storePath, loadImmediately: false);
            _ = store.LoadAsync();

            await Task.WhenAll(
                store.ScheduleSave(newVideoPath, new PlaybackProgress(654.5, 0.3)),
                store.ScheduleRemove(existingVideoPath));
            await store.FlushAsync();

            var reloadedStore = new PlaybackProgressStore(storePath);
            Assert.Equal(new PlaybackProgress(0), reloadedStore.Get(existingVideoPath));
            Assert.Equal(new PlaybackProgress(654.5, 0.3), reloadedStore.Get(newVideoPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_DoesNotRewriteProgressForInsignificantPositionChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            store.Save(videoPath, new PlaybackProgress(100));
            var previousContents = File.ReadAllText(storePath);

            store.Save(videoPath, new PlaybackProgress(100.99));

            Assert.Equal(previousContents, File.ReadAllText(storePath));
            Assert.Equal(100, new PlaybackProgressStore(storePath).Get(videoPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_RewritesProgressWhenOnlyPlaybackSettingsChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            store.Save(videoPath, new PlaybackProgress(100, PlaybackSpeed: 1));

            store.Save(videoPath, new PlaybackProgress(100.1, PlaybackSpeed: 1.5));

            Assert.Equal(1.5, new PlaybackProgressStore(storePath).Get(videoPath).PlaybackSpeed);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduleSaveAndFlush_PersistTheLatestProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            _ = store.ScheduleSave(videoPath, new PlaybackProgress(80));
            _ = store.ScheduleSave(videoPath, new PlaybackProgress(120, 0.25));

            await store.FlushAsync();

            var savedProgress = new PlaybackProgressStore(storePath).Get(videoPath);
            Assert.Equal(new PlaybackProgress(120, 0.25), savedProgress);
            Assert.False(File.Exists(storePath + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduleRemoveAndFlush_RemoveTheSavedProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            await store.ScheduleSave(videoPath, new PlaybackProgress(80));
            await store.ScheduleRemove(videoPath);
            await store.FlushAsync();

            Assert.Equal(new PlaybackProgress(0), new PlaybackProgressStore(storePath).Get(videoPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentScheduledUpdates_FlushTheLatestStoreState()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            Parallel.For(0, 100, index =>
                _ = store.ScheduleSave(videoPath, new PlaybackProgress(100 + index * 2, index / 10d)));

            await store.FlushAsync();

            Assert.Equal(store.Get(videoPath), new PlaybackProgressStore(storePath).Get(videoPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduleSave_RetriesAnUnchangedProgressAfterPersistenceFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blockingFile = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockingFile, "block");
        var storePath = Path.Combine(blockingFile, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            var progress = new PlaybackProgress(80);
            var failedWrite = store.ScheduleSave(videoPath, progress);

            await Assert.ThrowsAsync<IOException>(() => failedWrite);

            File.Delete(blockingFile);
            await store.ScheduleSave(videoPath, progress);

            Assert.Equal(progress, new PlaybackProgressStore(storePath).Get(videoPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Remove_DeletesSavedProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(directory, "playback.json");
        var videoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var store = new PlaybackProgressStore(storePath);
            store.Save(videoPath, new PlaybackProgress(80));
            store.Remove(videoPath);

            Assert.Equal(0, new PlaybackProgressStore(storePath).Get(videoPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesEmptyProgressWhenStoreIsInvalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        try
        {
            File.WriteAllText(storePath, "{");

            var progress = new PlaybackProgressStore(storePath).Get(Path.Combine(directory, "episode.mkv"));

            Assert.Equal(new PlaybackProgress(0), progress);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_SkipsAnInvalidPathAndPreservesLaterEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var validVideoPath = Path.Combine(directory, "episode.mkv");
        try
        {
            var history = new Dictionary<string, PlaybackProgress>
            {
                [new string('x', 40_000)] = new(10),
                [validVideoPath] = new(55)
            };
            File.WriteAllText(storePath, JsonSerializer.Serialize(history));

            var progress = new PlaybackProgressStore(storePath).Get(validVideoPath);

            Assert.Equal(55, progress.PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_SkipsAnInvalidEntryValueAndPreservesOtherEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "playback.json");
        var firstVideoPath = Path.Combine(directory, "first.mkv");
        var invalidVideoPath = Path.Combine(directory, "invalid.mkv");
        var lastVideoPath = Path.Combine(directory, "last.mkv");
        try
        {
            var json = "{" + JsonSerializer.Serialize(firstVideoPath) + ":{\"PositionSeconds\":111},"
                + JsonSerializer.Serialize(invalidVideoPath) + ":\"invalid\","
                + JsonSerializer.Serialize(lastVideoPath) + ":{\"PositionSeconds\":333}}";
            File.WriteAllText(storePath, json);

            var store = new PlaybackProgressStore(storePath);

            Assert.Equal(111, store.Get(firstVideoPath).PositionSeconds);
            Assert.Equal(0, store.Get(invalidVideoPath).PositionSeconds);
            Assert.Equal(333, store.Get(lastVideoPath).PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static int CountStoredEntries(string storePath) =>
        JsonSerializer.Deserialize<Dictionary<string, PlaybackProgress>>(File.ReadAllText(storePath))!.Count;
}
