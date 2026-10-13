namespace VideoPlayer.Core;

public sealed class CoalescingRefreshGate(bool initiallyPending = false)
{
    private int _isPending = initiallyPending ? 1 : 0;
    private int _isScheduled;

    public bool IsPending => Volatile.Read(ref _isPending) != 0;

    public bool Request()
    {
        Interlocked.Exchange(ref _isPending, 1);
        return TrySchedule();
    }

    public bool ConsumePending() => Interlocked.Exchange(ref _isPending, 0) != 0;

    public void MarkPendingForFallback() => Interlocked.Exchange(ref _isPending, 1);

    public bool Complete(bool allowReschedule)
    {
        Volatile.Write(ref _isScheduled, 0);
        return allowReschedule && IsPending && TrySchedule();
    }

    private bool TrySchedule() => Interlocked.CompareExchange(ref _isScheduled, 1, 0) == 0;
}
