using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Services.Cluster;

/// <summary>
///     Single source of truth for the transfer chunk-size envelope. The
///     worker's ReceiveFile endpoint sizes its Kestrel request limit from
///     <see cref="MaxChunkRequestBytes"/>, and the settings validation / UI /
///     throttle all clamp to <see cref="MaxChunkSizeMB"/> — deriving both
///     from one constant guarantees a user-configured chunk can never exceed
///     what the receiving endpoint accepts (which used to 413 every chunk
///     above ~71 MB and stall uploads in a 5-minute retry loop).
/// </summary>
public static class TransferLimits
{
    /// <summary> Smallest configurable chunk size, in MB. </summary>
    public const int MinChunkSizeMB = 4;

    /// <summary> Largest configurable chunk size, in MB. </summary>
    public const int MaxChunkSizeMB = 256;

    /// <summary> Request-body cap for the chunk-receive endpoint: max chunk + headroom for headers/encoding. </summary>
    public const long MaxChunkRequestBytes = (long)MaxChunkSizeMB * 1024 * 1024 + 8_000_000;
}

/// <summary>
///     Master-side gate for cluster file transfers. Two independent layers:
///
///     <list type="bullet">
///         <item><description><b>Concurrency caps</b> — gate how many uploads/downloads can be in flight at once, cluster-wide and per-node. Tracked via atomic in-flight counters; cap is read fresh from the live settings snapshot on every acquire attempt.</description></item>
///         <item><description><b>Bandwidth caps</b> — gate the rate at which bytes flow, cluster-wide and per-node, via <see cref="TokenBucketRateLimiter"/>. Each chunk acquires tokens equal to its byte count before the master sends it.</description></item>
///     </list>
///
///     <para>A cap of <c>0</c> means "unlimited" and the corresponding gate
///     is bypassed entirely (no acquire). Settings changes take effect on the
///     next acquire attempt without rebuilding any state — the in-flight
///     counter simply gets compared against the new cap on the next CAS try.
///     Holders that are over the new cap stay in flight until they release
///     naturally; new acquires queue until the counter drops below the new
///     cap. This is the only way to safely live-mutate a concurrency cap
///     without losing track of who's currently holding a slot.</para>
///
///     <para>This service is orthogonal to the <c>SlotLedger</c>: the ledger
///     decides whether a job can be dispatched at all (per-device hardware
///     capacity); the throttle decides how many of those dispatched jobs
///     can transfer bytes concurrently and how fast.</para>
/// </summary>
public sealed class TransferThrottle : IDisposable
{
    // Replenishment cadence for the token buckets. 100ms keeps cap accuracy
    // tight without chattering.
    private const int ReplenishMs = 100;

    // Bandwidth acquires are sliced into pieces this size so a single call
    // never asks the bucket for more permits than it can hold. The bucket's
    // TokenLimit is the byte-equivalent of one second of throughput, so a
    // request larger than that throws ArgumentOutOfRangeException — which
    // breaks any chunk size larger than the configured MB/s cap. 1 MB is
    // small enough to fit the smallest meaningful cap (1 MB/s) and big
    // enough to keep acquire overhead negligible.
    private const int BandwidthSliceBytes = 1024 * 1024;

    // Poll interval for concurrency acquires that are over-cap. Re-checks
    // the live cap and the in-flight count every tick. 250ms keeps latency
    // tolerable when a slot frees up; the cost is one CAS read per waiter
    // per tick, which is negligible.
    private const int ConcurrencyPollMs = 250;

    private readonly NetworkingSettingsService _settings;

    // Concurrency counters. A single mutable int per scope, mutated only via
    // Interlocked.CompareExchange so a save→read race can't double-grant a
    // slot. Per-node counters are lazily added; never removed except by
    // ForgetNode (which is fine — a stale counter at 0 occupies ~16 bytes).
    private readonly Counter _globalUploadInFlight   = new();
    private readonly Counter _globalDownloadInFlight = new();
    private readonly ConcurrentDictionary<string, Counter> _perNodeUploadInFlight   = new();
    private readonly ConcurrentDictionary<string, Counter> _perNodeDownloadInFlight = new();

    // Bandwidth limiters. Replaced on settings change; the slice loop in
    // AcquireSlicedAsync re-reads each iteration so a swap mid-chunk takes
    // effect on the next 1 MB slice rather than killing the upload.
    private readonly object _bandwidthLock = new();
    private RateLimiter?    _globalUploadRate;
    private RateLimiter?    _globalDownloadRate;
    private readonly ConcurrentDictionary<string, RateLimiter> _perNodeUploadRates   = new();
    private readonly ConcurrentDictionary<string, RateLimiter> _perNodeDownloadRates = new();

    // Tracks the configured per-node MB/s caps each per-node bucket was
    // built with. When a settings change moves the cap, we rebuild the
    // affected buckets; an unchanged cap leaves them alone.
    private readonly ConcurrentDictionary<string, int> _perNodeUploadRateCaps   = new();
    private readonly ConcurrentDictionary<string, int> _perNodeDownloadRateCaps = new();

    private NetworkingSettings _snapshot;

    public TransferThrottle(NetworkingSettingsService settings)
    {
        _settings = settings;
        _snapshot = settings.Get();
        ApplyBandwidth(_snapshot, previous: null);
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(NetworkingSettings updated)
    {
        var previous = _snapshot;
        _snapshot = updated;
        ApplyBandwidth(updated, previous);
        // Concurrency caps need no rebuild — counters are persistent and
        // every acquire re-reads the cap from _snapshot.
    }

    /// <summary>
    ///     Rebuilds bandwidth limiters that were affected by a settings change.
    ///     Per-node buckets whose configured cap is unchanged are kept; this
    ///     avoids resetting the bucket fill on unrelated saves (e.g. saving
    ///     a chunk-size change shouldn't disrupt an active transfer's pacing).
    /// </summary>
    private void ApplyBandwidth(NetworkingSettings v, NetworkingSettings? previous)
    {
        lock (_bandwidthLock)
        {
            if (previous == null || previous.MaxUploadMBps != v.MaxUploadMBps)
                ReplaceRateLimiter(ref _globalUploadRate, v.MaxUploadMBps);
            if (previous == null || previous.MaxDownloadMBps != v.MaxDownloadMBps)
                ReplaceRateLimiter(ref _globalDownloadRate, v.MaxDownloadMBps);
        }

        if (previous == null || previous.MaxUploadMBpsPerNode != v.MaxUploadMBpsPerNode)
            FlushPerNodeRates(_perNodeUploadRates, _perNodeUploadRateCaps);
        if (previous == null || previous.MaxDownloadMBpsPerNode != v.MaxDownloadMBpsPerNode)
            FlushPerNodeRates(_perNodeDownloadRates, _perNodeDownloadRateCaps);
    }

    private static void FlushPerNodeRates(
        ConcurrentDictionary<string, RateLimiter> dict,
        ConcurrentDictionary<string, int> capDict)
    {
        foreach (var kv in dict.ToArray())
        {
            if (dict.TryRemove(kv.Key, out var stale)) stale.Dispose();
            capDict.TryRemove(kv.Key, out _);
        }
    }

    private static void ReplaceRateLimiter(ref RateLimiter? slot, int megabytesPerSecond)
    {
        slot?.Dispose();
        slot = megabytesPerSecond > 0 ? BuildBucket(megabytesPerSecond) : null;
    }

    private static RateLimiter BuildBucket(int megabytesPerSecond)
    {
        long bytesPerSecond = (long)megabytesPerSecond * 1024 * 1024;
        long perPeriod      = Math.Max(1, bytesPerSecond / (1000 / ReplenishMs));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit           = (int)Math.Min(int.MaxValue, bytesPerSecond),
            TokensPerPeriod      = (int)Math.Min(int.MaxValue, perPeriod),
            ReplenishmentPeriod  = TimeSpan.FromMilliseconds(ReplenishMs),
            QueueLimit           = int.MaxValue,
            AutoReplenishment    = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>
    ///     Acquires an upload concurrency slot for <paramref name="nodeId"/>.
    ///     Awaits until both the cluster-wide and per-node caps have headroom.
    ///     Disposing the returned handle releases both. Cancellation is honored.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireUploadAsync(string nodeId, CancellationToken ct)
    {
        Action? globalRelease = await AcquireCounterAsync(
            _globalUploadInFlight, () => _snapshot.MaxConcurrentUploads, ct).ConfigureAwait(false);

        Action? perNodeRelease;
        try
        {
            perNodeRelease = await AcquireCounterAsync(
                GetCounter(_perNodeUploadInFlight, nodeId),
                () => _snapshot.MaxConcurrentUploadsPerNode, ct).ConfigureAwait(false);
        }
        catch
        {
            globalRelease?.Invoke();
            throw;
        }

        return new ReleaseHandle(() =>
        {
            perNodeRelease?.Invoke();
            globalRelease?.Invoke();
        });
    }

    /// <summary> Symmetric with <see cref="AcquireUploadAsync"/> for downloads. </summary>
    public async Task<IAsyncDisposable> AcquireDownloadAsync(string nodeId, CancellationToken ct)
    {
        Action? globalRelease = await AcquireCounterAsync(
            _globalDownloadInFlight, () => _snapshot.MaxConcurrentDownloads, ct).ConfigureAwait(false);

        Action? perNodeRelease;
        try
        {
            perNodeRelease = await AcquireCounterAsync(
                GetCounter(_perNodeDownloadInFlight, nodeId),
                () => _snapshot.MaxConcurrentDownloadsPerNode, ct).ConfigureAwait(false);
        }
        catch
        {
            globalRelease?.Invoke();
            throw;
        }

        return new ReleaseHandle(() =>
        {
            perNodeRelease?.Invoke();
            globalRelease?.Invoke();
        });
    }

    /// <summary>
    ///     Acquires a slot on <paramref name="counter"/> while the live cap
    ///     from <paramref name="capResolver"/> has headroom. Returns
    ///     <see langword="null"/> when the cap is <c>0</c> (unlimited) — the
    ///     caller short-circuits the release path on null. Otherwise returns
    ///     a release action that decrements the counter exactly once.
    ///
    ///     <para>Implementation: a CAS-only acquire avoids the increment-and-rollback
    ///     pattern that would let a transient over-cap state confuse other
    ///     waiters. The cap is re-read every iteration so a live settings
    ///     change (raise, lower, or unlimited) takes effect on the next poll
    ///     without any explicit "wake" from the writer.</para>
    /// </summary>
    private static async Task<Action?> AcquireCounterAsync(
        Counter counter, Func<int> capResolver, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int cap = capResolver();
            if (cap <= 0)
            {
                // Unlimited: take no slot, return no release action.
                return null;
            }

            // CAS-only acquire: read current, only commit increment if it
            // would land at-or-below cap. Lost CAS races spin without sleep
            // because they imply someone else just grabbed/released a slot
            // and we should re-evaluate immediately.
            int current = Volatile.Read(ref counter.Value);
            if (current < cap)
            {
                int prev = Interlocked.CompareExchange(ref counter.Value, current + 1, current);
                if (prev == current)
                {
                    var c = counter; // capture for the closure
                    return () => Interlocked.Decrement(ref c.Value);
                }
                // CAS lost — retry without sleeping; the new value is
                // already in `prev` but reading it again is cheap and keeps
                // the loop body simple.
                continue;
            }

            // At cap. Sleep before re-checking so we don't spin a CPU.
            await Task.Delay(ConcurrencyPollMs, ct).ConfigureAwait(false);
        }
    }

    private static Counter GetCounter(ConcurrentDictionary<string, Counter> dict, string nodeId) =>
        dict.GetOrAdd(nodeId, _ => new Counter());

    /// <summary>
    ///     Awaits enough tokens to send <paramref name="bytes"/> through the
    ///     upload bandwidth bucket. Acquires from cluster-wide first, then
    ///     per-node — slowest cap wins. Returns immediately when both caps
    ///     are unlimited.
    /// </summary>
    public async Task AcquireUploadBandwidthAsync(string nodeId, int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        await AcquireSlicedAsync(
            () => _globalUploadRate,
            () => GetPerNodeRateLimiter(_perNodeUploadRates, _perNodeUploadRateCaps, nodeId, _snapshot.MaxUploadMBpsPerNode),
            bytes, ct).ConfigureAwait(false);
    }

    /// <summary> Symmetric with <see cref="AcquireUploadBandwidthAsync"/> for downloads. </summary>
    public async Task AcquireDownloadBandwidthAsync(string nodeId, int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        await AcquireSlicedAsync(
            () => _globalDownloadRate,
            () => GetPerNodeRateLimiter(_perNodeDownloadRates, _perNodeDownloadRateCaps, nodeId, _snapshot.MaxDownloadMBpsPerNode),
            bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Acquires <paramref name="bytes"/> in 1 MB slices, re-resolving the
    ///     limiters each iteration so a settings change mid-transfer takes
    ///     effect on the very next slice rather than blocking on a disposed
    ///     bucket. <see cref="ObjectDisposedException"/> from a limiter that
    ///     was replaced during the await is swallowed — the next iteration
    ///     reads the new (possibly null) limiter and proceeds accordingly.
    /// </summary>
    private static async Task AcquireSlicedAsync(
        Func<RateLimiter?> globalResolver,
        Func<RateLimiter?> perNodeResolver,
        int bytes, CancellationToken ct)
    {
        int remaining = bytes;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int take = Math.Min(BandwidthSliceBytes, remaining);
            var global  = globalResolver();
            var perNode = perNodeResolver();
            if (global == null && perNode == null)
            {
                return;
            }

            if (global != null)
            {
                try
                {
                    using var lease = await global.AcquireAsync(take, ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { /* swapped under us — re-read on next slice */ }
            }
            if (perNode != null)
            {
                try
                {
                    using var lease = await perNode.AcquireAsync(take, ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { /* same */ }
            }
            remaining -= take;
        }
    }

    /// <summary>
    ///     Drops the per-node counters and rate-limiters for a removed node.
    ///     Called from <c>ClusterService</c> when a node disconnects permanently.
    ///     Concurrency counters at 0 would not leak meaningfully on their own,
    ///     but we clear them anyway so a node rejoin starts from a clean slate.
    /// </summary>
    public void ForgetNode(string nodeId)
    {
        _perNodeUploadInFlight.TryRemove(nodeId, out _);
        _perNodeDownloadInFlight.TryRemove(nodeId, out _);
        if (_perNodeUploadRates.TryRemove(nodeId, out var r1)) r1.Dispose();
        if (_perNodeDownloadRates.TryRemove(nodeId, out var r2)) r2.Dispose();
        _perNodeUploadRateCaps.TryRemove(nodeId, out _);
        _perNodeDownloadRateCaps.TryRemove(nodeId, out _);
    }

    private static RateLimiter? GetPerNodeRateLimiter(
        ConcurrentDictionary<string, RateLimiter> dict,
        ConcurrentDictionary<string, int> capDict,
        string nodeId, int megabytesPerSecond)
    {
        if (megabytesPerSecond <= 0)
        {
            // Cap removed for this scope. If a stale bucket exists from a
            // prior cap, dispose it now so the next non-zero cap rebuilds.
            if (dict.TryRemove(nodeId, out var stale)) stale.Dispose();
            capDict.TryRemove(nodeId, out _);
            return null;
        }
        // Cap-aware GetOrAdd: if an existing bucket was built for a
        // different cap, replace it. Buckets are rebuilt only when the cap
        // numerically changes; saving an unrelated setting leaves them.
        if (dict.TryGetValue(nodeId, out var existing)
            && capDict.TryGetValue(nodeId, out var existingCap)
            && existingCap == megabytesPerSecond)
        {
            return existing;
        }
        var fresh = BuildBucket(megabytesPerSecond);
        if (dict.TryRemove(nodeId, out var oldOne)) oldOne.Dispose();
        dict[nodeId] = fresh;
        capDict[nodeId] = megabytesPerSecond;
        return fresh;
    }

    /// <summary>
    ///     The chunk size the master should use for uploads, in bytes. Lives
    ///     here so callers don't need to read settings directly.
    /// </summary>
    public int ChunkSizeBytes => Math.Clamp(_snapshot.ChunkSizeMB,
        TransferLimits.MinChunkSizeMB, TransferLimits.MaxChunkSizeMB) * 1024 * 1024;

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _globalUploadRate?.Dispose();
        _globalDownloadRate?.Dispose();
        foreach (var kv in _perNodeUploadRates.ToArray())
            if (_perNodeUploadRates.TryRemove(kv.Key, out var r)) r.Dispose();
        foreach (var kv in _perNodeDownloadRates.ToArray())
            if (_perNodeDownloadRates.TryRemove(kv.Key, out var r)) r.Dispose();
    }

    /// <summary>
    ///     Reference-typed integer wrapper so per-node counters can be mutated
    ///     by ref via Interlocked. <see cref="ConcurrentDictionary{TKey,TValue}"/>
    ///     hands back values, not refs, so a raw <c>int</c> wouldn't work.
    /// </summary>
    private sealed class Counter
    {
        public int Value;
    }

    private sealed class ReleaseHandle : IAsyncDisposable
    {
        private Action? _release;
        public ReleaseHandle(Action release) => _release = release;
        public ValueTask DisposeAsync()
        {
            var r = Interlocked.Exchange(ref _release, null);
            r?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
