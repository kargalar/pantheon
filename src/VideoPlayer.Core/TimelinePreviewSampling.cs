namespace VideoPlayer.Core;

public readonly record struct TimelinePreviewSample(long Index, double PositionSeconds);

public static class TimelinePreviewSampling
{
    public const int VisibleSampleCount = 9;
    public const int DesiredVideoSamples = 100;
    public const double MinimumIntervalSeconds = 1;
    public const double MaximumIntervalSeconds = 15;

    public static double GetInterval(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return MinimumIntervalSeconds;
        return Math.Clamp(durationSeconds / DesiredVideoSamples, MinimumIntervalSeconds, MaximumIntervalSeconds);
    }

    public static IReadOnlyList<TimelinePreviewSample> GetSamples(double positionSeconds, double durationSeconds)
    {
        Span<TimelinePreviewSample> samples = stackalloc TimelinePreviewSample[VisibleSampleCount];
        var sampleCount = FillSamples(positionSeconds, durationSeconds, samples);
        if (sampleCount == 0) return [];
        return samples[..sampleCount].ToArray();
    }

    public static int FillSamples(
        double positionSeconds,
        double durationSeconds,
        Span<TimelinePreviewSample> destination)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        if (destination.Length < VisibleSampleCount)
            throw new ArgumentException($"The destination must hold at least {VisibleSampleCount} samples.", nameof(destination));

        var interval = GetInterval(durationSeconds);
        var lastIndex = (long)Math.Floor(durationSeconds / interval);
        var visibleCount = (int)Math.Min(VisibleSampleCount, lastIndex + 1);
        var centeredIndex = (long)Math.Floor(Math.Clamp(positionSeconds, 0, durationSeconds) / interval + 0.5);
        centeredIndex = Math.Clamp(centeredIndex, 0, lastIndex);
        var firstIndex = Math.Clamp(centeredIndex - visibleCount / 2, 0, lastIndex - visibleCount + 1);

        for (var offset = 0; offset < visibleCount; offset++)
        {
            var index = firstIndex + offset;
            destination[offset] = new TimelinePreviewSample(index, Math.Min(index * interval, durationSeconds));
        }

        return visibleCount;
    }

    public static TimelinePreviewSample GetClosestSample(
        ReadOnlySpan<TimelinePreviewSample> samples,
        double positionSeconds)
    {
        if (samples.IsEmpty) return default;
        var closestSample = samples[0];
        var closestDistance = Math.Abs(closestSample.PositionSeconds - positionSeconds);
        for (var index = 1; index < samples.Length; index++)
        {
            var sample = samples[index];
            var distance = Math.Abs(sample.PositionSeconds - positionSeconds);
            if (distance >= closestDistance) continue;
            closestSample = sample;
            closestDistance = distance;
        }
        return closestSample;
    }

    public static TimelinePreviewSample GetClosestSample(
        IReadOnlyList<TimelinePreviewSample> samples,
        double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return default;
        var closestSample = samples[0];
        var closestDistance = Math.Abs(closestSample.PositionSeconds - positionSeconds);
        for (var index = 1; index < samples.Count; index++)
        {
            var sample = samples[index];
            var distance = Math.Abs(sample.PositionSeconds - positionSeconds);
            if (distance >= closestDistance) continue;
            closestSample = sample;
            closestDistance = distance;
        }
        return closestSample;
    }

    public static IReadOnlyList<TimelinePreviewSample> GetVisibleSamples(
        IReadOnlyList<TimelinePreviewSample> samples,
        ICollection<long> cachedIndexes,
        long centerIndex,
        bool includePlaceholders)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(cachedIndexes);
        if (!cachedIndexes.Contains(centerIndex)) return [];
        if (includePlaceholders) return samples;

        var visibleCount = 0;
        for (var index = 0; index < samples.Count; index++)
            if (cachedIndexes.Contains(samples[index].Index)) visibleCount++;
        if (visibleCount == 0) return [];
        var visibleSamples = new TimelinePreviewSample[visibleCount];
        var visibleIndex = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (cachedIndexes.Contains(sample.Index)) visibleSamples[visibleIndex++] = sample;
        }
        return visibleSamples;
    }
}
