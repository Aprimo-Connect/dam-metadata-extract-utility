using System.Diagnostics;

namespace AprimoExport.Http;

/// <summary>
/// Token-bucket rate limiter with an independent concurrency cap.
///
/// <para><c>RequestsPerSecond</c> sets the sustained rate and may be fractional
/// (0.5 = one request every two seconds). <c>Burst</c> is the bucket depth: how many
/// requests may fire back-to-back after an idle period. Burst 1 gives strictly even
/// spacing, which is the friendliest to a shared tenant.</para>
///
/// <para>Pacing is applied to request *starts*, serialised through a mutex so the
/// emitted rate is accurate regardless of how many callers are waiting.</para>
/// </summary>
public sealed class RateLimiter : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SemaphoreSlim _concurrency;
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly bool _enabled;

    private double _tokens;
    private long _lastRefillTimestamp;

    /// <summary>Server-requested cool-off (from Retry-After); no request starts before this.</summary>
    private long _notBeforeTimestamp;

    public RateLimiter(double requestsPerSecond, int burst, int maxConcurrentRequests)
    {
        _enabled = requestsPerSecond > 0;
        _ratePerSecond = requestsPerSecond;
        _capacity = Math.Max(1, burst);
        _tokens = _capacity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
        _notBeforeTimestamp = 0;
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrentRequests));
    }

    public double RequestsPerSecond => _ratePerSecond;
    public bool Enabled => _enabled;

    /// <summary>
    /// Waits for a rate-limit slot and a concurrency slot. Dispose the returned handle
    /// once the request completes to release the concurrency slot.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (_enabled)
        {
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var now = Stopwatch.GetTimestamp();

                    // Honour a server-requested cool-off before spending tokens.
                    if (_notBeforeTimestamp > now)
                    {
                        var penalty = ToSeconds(_notBeforeTimestamp - now);
                        await Task.Delay(ToDelay(penalty), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var elapsed = ToSeconds(now - _lastRefillTimestamp);
                    if (elapsed > 0)
                    {
                        _tokens = Math.Min(_capacity, _tokens + elapsed * _ratePerSecond);
                        _lastRefillTimestamp = now;
                    }

                    if (_tokens >= 1.0)
                    {
                        _tokens -= 1.0;
                        break;
                    }

                    var waitSeconds = (1.0 - _tokens) / _ratePerSecond;
                    await Task.Delay(ToDelay(waitSeconds), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(_concurrency);
    }

    /// <summary>
    /// Applies backpressure after a 429/503: empties the bucket and blocks all starts
    /// for <paramref name="delay"/>. Every caller backs off, not just the one that
    /// got throttled.
    /// </summary>
    public async Task PenalizeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (!_enabled || delay <= TimeSpan.Zero) return;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            if (target > _notBeforeTimestamp) _notBeforeTimestamp = target;
            _tokens = 0;
            _lastRefillTimestamp = Stopwatch.GetTimestamp();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    // Task.Delay has ~15 ms granularity on Windows; clamp to a sane floor so tight
    // loops do not spin, and the limiter stays accurate over time via token accrual.
    private static TimeSpan ToDelay(double seconds) =>
        TimeSpan.FromMilliseconds(Math.Max(1.0, seconds * 1000.0));

    public void Dispose()
    {
        _mutex.Dispose();
        _concurrency.Dispose();
    }

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Slot(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
