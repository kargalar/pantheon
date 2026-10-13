using System.Diagnostics;
using VideoPlayer;

const int queueSize = 100_000;
var paths = Enumerable.Range(0, queueSize)
    .Select(index => Path.Combine("C:\\Videos\\Season 01", $"Episode {index:D5}.mkv"))
    .ToArray();

var legacy = Measure("eager row models", () => paths
    .Select((path, index) => new LegacyQueueListItem(Path.GetFileName(path), index))
    .ToArray());
var optimized = Measure("lazy-name value rows", () => paths
    .Select((path, index) => new QueueListItem(path, index))
    .ToArray());

if (legacy.Rows.Length != optimized.Rows.Length)
    throw new InvalidOperationException("Queue row counts do not match.");

for (var index = 0; index < queueSize; index++)
{
    var expected = legacy.Rows[index];
    var actual = optimized.Rows[index];
    if (expected.Index != actual.Index || !string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
        throw new InvalidOperationException($"Queue row {index} differs from the eager baseline.");
}

Console.WriteLine(
    $"allocationReductionPercent={100.0 * (legacy.AllocatedBytes - optimized.AllocatedBytes) / legacy.AllocatedBytes:F1}");
if (optimized.AllocatedBytes >= legacy.AllocatedBytes)
    throw new InvalidOperationException("The optimized queue rows did not reduce allocations.");

static (T[] Rows, long AllocatedBytes, double ElapsedMilliseconds) Measure<T>(string name, Func<T[]> build)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    var rows = build();
    timer.Stop();
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine(
        $"{name}: rows={rows.Length:N0}, elapsedMs={timer.Elapsed.TotalMilliseconds:F2}, allocatedBytes={allocatedBytes:N0}");
    return (rows, allocatedBytes, timer.Elapsed.TotalMilliseconds);
}

sealed record LegacyQueueListItem(string Name, int Index);
