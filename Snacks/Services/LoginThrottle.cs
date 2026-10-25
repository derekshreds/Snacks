using System.Collections.Concurrent;

namespace Snacks.Services;

/// <summary>
///     Counts failed sign-ins per client (keyed by <see cref="ClientNetwork.GetRateLimitKey"/>)
///     and refuses further attempts once a client exceeds <see cref="MaxFailures"/> inside
///     <see cref="Window"/>. Successful sign-ins are never counted, so scripts and users that
///     log in often with the right password are unaffected.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowStart)> _failures = new();
    private readonly TimeProvider _time;

    public LoginThrottle() : this(TimeProvider.System) { }

    internal LoginThrottle(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary> Seconds until <paramref name="client"/> may try again, or 0 when it isn't blocked. </summary>
    public int RetryAfterSeconds(string client)
    {
        if (!_failures.TryGetValue(client, out var entry)) return 0;

        var remaining = entry.WindowStart + Window - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _failures.TryRemove(client, out _);
            return 0;
        }

        return entry.Failures >= MaxFailures ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
    }

    /// <summary> Records one failed attempt; the first failure opens a new window. </summary>
    public void RecordFailure(string client)
    {
        var now = _time.GetUtcNow();
        _failures.AddOrUpdate(
            client,
            _ => (1, now),
            (_, entry) => now - entry.WindowStart >= Window ? (1, now) : (entry.Failures + 1, entry.WindowStart));

        // A scan from many addresses can't grow the table without bound.
        if (_failures.Count > PruneThreshold)
        {
            foreach (var (key, entry) in _failures)
                if (now - entry.WindowStart >= Window) _failures.TryRemove(key, out _);
        }
    }
}
