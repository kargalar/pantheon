using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class TimelinePreviewSamplingTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(600, 6)]
    [InlineData(7200, 15)]
    [InlineData(0, 1)]
    public void IntervalScalesWithVideoDuration(double duration, double expected) =>
        Assert.Equal(expected, TimelinePreviewSampling.GetInterval(duration));

    [Theory]
    [InlineData(0, 2, 3)]
    [InlineData(50, 100, 9)]
    [InlineData(100, 100, 9)]
    [InlineData(1, 2, 3)]
    public void SamplesShowNearbyFramesAndStayInsideTheVideo(double position, double duration, int expectedCount)
    {
        var samples = TimelinePreviewSampling.GetSamples(position, duration);

        Assert.Equal(expectedCount, samples.Count);
        Assert.All(samples, sample => Assert.InRange(sample.PositionSeconds, 0, duration));
        Assert.Equal(samples.OrderBy(sample => sample.PositionSeconds), samples);
        Assert.Contains(samples, sample => Math.Abs(sample.PositionSeconds - position) <= TimelinePreviewSampling.GetInterval(duration));
    }

    [Fact]
    public void HundredSecondVideoUsesOneSecondStepsAndCentersAroundTheSeekPosition()
    {
        var samples = TimelinePreviewSampling.GetSamples(50, 100);

        Assert.Equal(new[] { 46d, 47d, 48d, 49d, 50d, 51d, 52d, 53d, 54d }, samples.Select(sample => sample.PositionSeconds));
    }

    [Fact]
    public void FillSamplesMatchesAllocatedSamplingForReusableBuffers()
    {
        var expected = TimelinePreviewSampling.GetSamples(50, 100);
        Span<TimelinePreviewSample> destination = stackalloc TimelinePreviewSample[TimelinePreviewSampling.VisibleSampleCount];

        var count = TimelinePreviewSampling.FillSamples(50, 100, destination);

        Assert.Equal(expected, destination[..count].ToArray());
        Assert.Equal(50, TimelinePreviewSampling.GetClosestSample(destination[..count], 50).Index);
    }

    [Fact]
    public void FillSamplesRejectsBuffersThatCannotHoldTheVisibleWindow()
    {
        Assert.Throws<ArgumentException>(FillWithShortBuffer);

        static void FillWithShortBuffer()
        {
            Span<TimelinePreviewSample> destination = stackalloc TimelinePreviewSample[TimelinePreviewSampling.VisibleSampleCount - 1];
            TimelinePreviewSampling.FillSamples(50, 100, destination);
        }
    }

    [Fact]
    public void VisibleSamplesWaitForCenterThenKeepNeighborSlotsStableWhileLoading()
    {
        var samples = TimelinePreviewSampling.GetSamples(50, 100);

        Assert.Empty(TimelinePreviewSampling.GetVisibleSamples(samples, [48, 49, 51, 52], 50, includePlaceholders: true));
        Assert.Equal(samples, TimelinePreviewSampling.GetVisibleSamples(samples, [50], 50, includePlaceholders: true));
        Assert.Equal(samples.Where(sample => sample.Index is 49 or 50 or 52),
            TimelinePreviewSampling.GetVisibleSamples(samples, [49, 50, 52], 50, includePlaceholders: false));
    }

    [Fact]
    public void ClosestSampleKeepsEarlierSampleWhenDistancesTie()
    {
        var samples = TimelinePreviewSampling.GetSamples(50, 100);

        Assert.Equal(49, TimelinePreviewSampling.GetClosestSample(samples, 49.5).Index);
    }
}
