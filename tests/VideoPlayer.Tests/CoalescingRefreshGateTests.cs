using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class CoalescingRefreshGateTests
{
    [Fact]
    public void RequestCoalescesEventsUntilTheScheduledRefreshCompletes()
    {
        var gate = new CoalescingRefreshGate();

        Assert.True(gate.Request());
        Assert.False(gate.Request());
        Assert.True(gate.ConsumePending());
        Assert.False(gate.IsPending);
        Assert.False(gate.Complete(allowReschedule: true));

        Assert.True(gate.Request());
        Assert.True(gate.ConsumePending());
        Assert.False(gate.Complete(allowReschedule: true));
    }

    [Fact]
    public void EventArrivingDuringRefreshSchedulesOneFollowUpRefresh()
    {
        var gate = new CoalescingRefreshGate();

        Assert.True(gate.Request());
        Assert.True(gate.ConsumePending());
        Assert.False(gate.Request());

        Assert.True(gate.Complete(allowReschedule: true));
        Assert.True(gate.IsPending);
        Assert.True(gate.ConsumePending());
        Assert.False(gate.Complete(allowReschedule: true));
    }

    [Fact]
    public void FailedRefreshKeepsRequestForPeriodicFallbackWithoutBusyRescheduling()
    {
        var gate = new CoalescingRefreshGate();

        Assert.True(gate.Request());
        Assert.True(gate.ConsumePending());
        gate.MarkPendingForFallback();

        Assert.False(gate.Complete(allowReschedule: false));
        Assert.True(gate.IsPending);
        Assert.True(gate.ConsumePending());
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void ConcurrentRequestsAcquireOnlyOneScheduledRefresh()
    {
        var gate = new CoalescingRefreshGate();
        var scheduledRefreshes = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (gate.Request()) Interlocked.Increment(ref scheduledRefreshes);
        });

        Assert.Equal(1, scheduledRefreshes);
        Assert.True(gate.IsPending);
    }
}
