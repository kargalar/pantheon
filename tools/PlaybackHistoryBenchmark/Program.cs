using System.Diagnostics;
using System.Text.Json;
using VideoPlayer.Core;

const int defaultEntryCount = 250_000;
const long maximumRetainedHeapBytes = 32 * 1024 * 1024;
var entryCount = args.Length == 0
    ? defaultEntryCount
    : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
if (entryCount < PlaybackProgressStore.MaximumEntryCount)
    throw new ArgumentOutOfRangeException(nameof(entryCount), "Entry count must exceed the retained history limit.");

var directory = Path.Combine(Path.GetTempPath(), $"playback-history-benchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "playback.json");
try
{
    WriteSyntheticHistory(storePath, directory, entryCount);
    var sourceLength = new FileInfo(storePath).Length;
    CollectGarbage();
    var heapBeforeLoad = GC.GetTotalMemory(forceFullCollection: true);
    var allocationsBeforeLoad = GC.GetAllocatedBytesForCurrentThread();

    var loadTimer = Stopwatch.StartNew();
    var store = new PlaybackProgressStore(storePath);
    loadTimer.Stop();
    var allocatedBytesDuringLoad = GC.GetAllocatedBytesForCurrentThread() - allocationsBeforeLoad;
    CollectGarbage();
    var heapAfterLoad = GC.GetTotalMemory(forceFullCollection: true);
    var retainedHeapBytes = Math.Max(0, heapAfterLoad - heapBeforeLoad);

    var latestEntry = Path.Combine(directory, $"episode-{entryCount - 1:D6}.mkv");
    var latestProgress = store.Get(latestEntry);
    store.FlushAsync().GetAwaiter().GetResult();
    var compactedLength = new FileInfo(storePath).Length;
    using var compactedHistory = JsonDocument.Parse(File.ReadAllBytes(storePath));
    var retainedEntryCount = compactedHistory.RootElement.EnumerateObject().Count();
    var oldestRetainedEntry = Path.Combine(directory, $"episode-{entryCount - PlaybackProgressStore.MaximumEntryCount:D6}.mkv");
    var trimmedEntry = Path.Combine(directory, $"episode-{entryCount - PlaybackProgressStore.MaximumEntryCount - 1:D6}.mkv");

    Console.WriteLine($"entries={entryCount:N0}, sourceBytes={sourceLength:N0}, compactedBytes={compactedLength:N0}");
    Console.WriteLine(
        $"loadAndTrimMs={loadTimer.Elapsed.TotalMilliseconds:F2}, allocatedManagedBytes={allocatedBytesDuringLoad:N0}, retainedManagedHeapBytes={retainedHeapBytes:N0}");
    Console.WriteLine($"retainedEntries={retainedEntryCount:N0}, latestEntryPositionSeconds={latestProgress.PositionSeconds:N0}");

    if (latestProgress.PositionSeconds != entryCount - 1)
        throw new InvalidOperationException("The newest history entry was not retained.");
    if (store.Get(oldestRetainedEntry).PositionSeconds != entryCount - PlaybackProgressStore.MaximumEntryCount
        || store.Get(trimmedEntry).PositionSeconds != 0
        || retainedEntryCount != PlaybackProgressStore.MaximumEntryCount)
        throw new InvalidOperationException("History trimming did not retain exactly the newest entries.");
    if (retainedHeapBytes > maximumRetainedHeapBytes)
        throw new InvalidOperationException($"Retained heap exceeded {maximumRetainedHeapBytes:N0} bytes.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static void WriteSyntheticHistory(string storePath, string directory, int entryCount)
{
    using var stream = new FileStream(storePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new Utf8JsonWriter(stream);
    writer.WriteStartObject();
    for (var index = 0; index < entryCount; index++)
    {
        writer.WritePropertyName(Path.Combine(directory, $"episode-{index:D6}.mkv"));
        writer.WriteStartObject();
        writer.WriteNumber("PositionSeconds", index);
        writer.WriteNumber("SubtitleDelaySeconds", 0);
        writer.WriteEndObject();
    }
    writer.WriteEndObject();
}

static void CollectGarbage()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
