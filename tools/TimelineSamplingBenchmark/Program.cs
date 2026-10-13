using System.Diagnostics;
using VideoPlayer.Core;

const int iterations = 200_000;
const double durationSeconds = 7_200;
var cachedIndexStore = new Dictionary<long, byte>();
var cachedIndexes = cachedIndexStore.Keys;

(long Checksum, long AllocatedBytes) baseline = RunBenchmark("LINQ baseline", LegacySamplingAndVisibility);
(long Checksum, long AllocatedBytes) optimized = RunBenchmark("optimized sampling", OptimizedSamplingAndVisibility);
if (baseline.Checksum != optimized.Checksum)
    throw new InvalidOperationException("The optimized sampling path produced a different checksum.");
if (optimized.AllocatedBytes != 0)
    throw new InvalidOperationException($"The optimized sampling path allocated {optimized.AllocatedBytes:N0} bytes.");

(long Checksum, long AllocatedBytes) RunBenchmark(string name, Func<int, long> operation)
{
    long checksum = 0;
    for (var index = 0; index < 20_000; index++) checksum += operation(index);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    for (var index = 0; index < iterations; index++) checksum += operation(index);
    timer.Stop();
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

    Console.WriteLine(
        $"{name}: iterations={iterations:N0}, elapsedMs={timer.Elapsed.TotalMilliseconds:F2}, "
        + $"allocatedBytes={allocatedBytes:N0}, checksum={checksum:N0}");
    return (checksum, allocatedBytes);
}

long LegacySamplingAndVisibility(int iteration)
{
    var interval = TimelinePreviewSampling.GetInterval(durationSeconds);
    const int sampleCount = TimelinePreviewSampling.VisibleSampleCount;
    var target = iteration * 13d % durationSeconds;
    var centeredIndex = (long)Math.Floor(Math.Clamp(target, 0, durationSeconds) / interval + 0.5);
    var lastIndex = (long)Math.Floor(durationSeconds / interval);
    var firstIndex = Math.Clamp(centeredIndex - sampleCount / 2, 0, lastIndex - sampleCount + 1);
    var samples = Enumerable.Range(0, sampleCount)
        .Select(offset =>
        {
            var index = firstIndex + offset;
            return new TimelinePreviewSample(index, Math.Min(index * interval, durationSeconds));
        })
        .ToArray();
    var centerIndex = samples.MinBy(sample => Math.Abs(sample.PositionSeconds - target)).Index;
    UpdateCachedIndexes(centerIndex);
    var visibleSamples = samples
        .Where(sample => cachedIndexes.Contains(sample.Index))
        .ToArray();
    return centerIndex + visibleSamples.Length;
}

long OptimizedSamplingAndVisibility(int iteration)
{
    var target = iteration * 13d % durationSeconds;
    Span<TimelinePreviewSample> sampleBuffer = stackalloc TimelinePreviewSample[TimelinePreviewSampling.VisibleSampleCount];
    var sampleCount = TimelinePreviewSampling.FillSamples(target, durationSeconds, sampleBuffer);
    var samples = sampleBuffer[..sampleCount];
    var centerIndex = TimelinePreviewSampling.GetClosestSample(samples, target).Index;
    UpdateCachedIndexes(centerIndex);
    var visibleCount = 0;
    if (cachedIndexes.Contains(centerIndex))
        for (var index = 0; index < samples.Length; index++)
            if (cachedIndexes.Contains(samples[index].Index)) visibleCount++;
    return centerIndex + visibleCount;
}

void UpdateCachedIndexes(long centerIndex)
{
    cachedIndexStore.Clear();
    cachedIndexStore[centerIndex - 1] = 0;
    cachedIndexStore[centerIndex] = 0;
    cachedIndexStore[centerIndex + 1] = 0;
}
