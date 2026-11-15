namespace Foretop.Core.Api;

public enum RequestPriority
{
    /// <summary>Players in the current battle.</summary>
    High = 0,

    /// <summary>Profiles the user opened, ship catalogue.</summary>
    Normal = 1,

    /// <summary>Recent stats, background refresh.</summary>
    Low = 2,
}

/// <summary>
/// Runs work items in priority order (FIFO within a priority), through one rate limiter,
/// with a cap on requests in flight. Each item is one HTTP request.
/// </summary>
public sealed class RequestScheduler : IDisposable
{
    private readonly PriorityQueue<WorkItem, (int Priority, long Seq)> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0, int.MaxValue);
    private readonly SemaphoreSlim _inFlight;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private long _seq;

    public RequestScheduler(RateLimiter limiter, int maxConcurrency = 4)
    {
        Limiter = limiter;
        _inFlight = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _loop = Task.Run(DispatchAsync);
    }

    public RateLimiter Limiter { get; }

    public int QueueLength
    {
        get { lock (_gate) return _queue.Count; }
    }

    public Task<T> Enqueue<T>(Func<CancellationToken, Task<T>> work, RequestPriority priority, CancellationToken ct)
    {
        var item = new WorkItem<T>(work, ct);
        lock (_gate) _queue.Enqueue(item, ((int)priority, _seq++));
        _available.Release();
        return item.Task;
    }

    private async Task DispatchAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a free slot, a queued item and the rate limiter first, and only then take the
                // highest-priority item. Dequeuing earlier would lock in whatever was queued first.
                await _inFlight.WaitAsync(ct).ConfigureAwait(false);
                WorkItem item;
                try
                {
                    await _available.WaitAsync(ct).ConfigureAwait(false);
                    await Limiter.WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate) item = _queue.Dequeue();
                }
                catch
                {
                    _inFlight.Release();
                    throw;
                }

                if (item.Token.IsCancellationRequested)
                {
                    item.Cancel();
                    _inFlight.Release();
                    continue;
                }

                _ = RunAsync(item);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunAsync(WorkItem item)
    {
        try { await item.RunAsync().ConfigureAwait(false); }
        finally { _inFlight.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lock (_gate)
        {
            while (_queue.TryDequeue(out var item, out _)) item.Cancel();
        }
        _cts.Dispose();
    }

    private abstract class WorkItem
    {
        protected WorkItem(CancellationToken token) => Token = token;
        public CancellationToken Token { get; }
        public abstract Task RunAsync();
        public abstract void Cancel();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<CancellationToken, Task<T>> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<CancellationToken, Task<T>> work, CancellationToken token) : base(token) => _work = work;

        public Task<T> Task => _tcs.Task;

        public override async Task RunAsync()
        {
            try { _tcs.TrySetResult(await _work(Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { _tcs.TrySetCanceled(Token); }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }

        public override void Cancel() => _tcs.TrySetCanceled(Token);
    }
}
