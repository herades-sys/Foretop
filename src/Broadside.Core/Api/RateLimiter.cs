namespace Broadside.Core.Api;

/// <summary>
/// Spaces requests evenly: at most <c>ratePerSecond</c> starts per second, no bursts.
/// Wargaming counts requests per second per IP, so even spacing is the safe shape.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private TimeSpan _interval;
    private long _nextSlotTicks;

    public RateLimiter(double ratePerSecond, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        SetRate(ratePerSecond);
    }

    public double RatePerSecond { get; private set; }

    public void SetRate(double ratePerSecond)
    {
        if (ratePerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
        lock (_gate)
        {
            RatePerSecond = ratePerSecond;
            _interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        }
    }

    /// <summary>Stop handing out slots for a while (after a REQUEST_LIMIT_EXCEEDED).</summary>
    public void Pause(TimeSpan duration)
    {
        lock (_gate)
        {
            var until = _time.GetUtcNow().UtcTicks + duration.Ticks;
            if (until > _nextSlotTicks) _nextSlotTicks = until;
        }
    }

    /// <summary>Waits until this caller's slot comes up.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcTicks;
            var slot = Math.Max(now, _nextSlotTicks);
            _nextSlotTicks = slot + _interval.Ticks;
            delay = TimeSpan.FromTicks(slot - now);
        }
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, ct);
    }
}
