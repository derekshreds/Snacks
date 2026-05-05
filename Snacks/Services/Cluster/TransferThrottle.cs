using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Services.Cluster;

/// <summary>
///     Master-side gate for cluster file transfers. Two independent layers:
///
///     <list type="bullet">
///         <item><description><b>Concurrency caps</b> — gate how many uploads/downloads can be in flight at once, cluster-wide and per-node, via <see cref="SemaphoreSlim"/>.</description></item>
///         <item><description><b>Bandwidth caps</b> — gate the rate at which bytes flow, cluster-wide and per-node, via <see cref="TokenBucketRateLimiter"/>. Each chunk acquires tokens equal to its byte count before the master sends it.</description></item>
///     </list>
///
///     <para>A cap of <c>0</c> means "unlimited" and the corresponding limiter
///     is bypassed entirely (no acquire). Settings are re-read live, so saving
///     a new cap rebuilds the bandwidth limiters and the next chunk picks up
///     the new rate without waiting for the current chunk to finish.</para>
///
///     <para>This service is orthogonal to the <c>SlotLedger</c>: the ledger
///     decides whether a job can be dispatched at all (per-device hardware
///     capacity); the throttle decides how many of those dispatched jobs
///     can transfer bytes concurrently and how fast.</para>
/// </summary>
public sealed class TransferThrottle : IDisposable
{
    // Replenishment cadence for the token buckets. 100ms keeps cap accuracy
    // tight without chattering. See plan doc for trade-off rationale.
    private const int ReplenishMs = 100;

    private readonly NetworkingSettingsService _settings;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perNodeUploadSems    = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perNodeDownloadSems  = new();
    private readonly ConcurrentDictionary<string, RateLimiter>   _perNodeUploadRates   = new();
    private readonly ConcurrentDictionary<string, RateLimiter>   _perNodeDownloadRates = new();

    // Global limiters are guarded by simple locks so save→rebuild is atomic
    // with the next acquire.
    private readonly object _globalLock = new();
    private SemaphoreSlim?  _globalUploadSem;     // null when MaxConcurrentUploads == 0
    private SemaphoreSlim?  _globalDownloadSem;
    private RateLimiter?    _globalUploadRate;    // null when MaxUploadMBps == 0
    private RateLimiter?    _globalDownloadRate;

    private NetworkingSettings _snapshot;

    public TransferThrottle(NetworkingSettingsService settings)
    {
        _settings = settings;
        _snapshot = settings.Get();
        ApplySettings(_snapshot);
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(NetworkingSettings updated)
    {
        _snapshot = updated;
        ApplySettings(updated);
    }

    private void ApplySettings(NetworkingSettings v)
    {
        lock (_globalLock)
        {
            ReplaceSemaphore(ref   _globalUploadSem,    v.MaxConcurrentUploads);
            ReplaceSemaphore(ref   _globalDownloadSem,  v.MaxConcurrentDownloads);
            ReplaceRateLimiter(ref _globalUploadRate,   v.MaxUploadMBps);
            ReplaceRateLimiter(ref _globalDownloadRate, v.MaxDownloadMBps);
        }
        // Per-node limiters are rebuilt on next acquire — easier than
        // reconciling against the live node set, and cheap because they're
        // lazy-created. Old per-node limiters dispose when replaced.
        FlushPerNode(_perNodeUploadSems);
        FlushPerNode(_perNodeDownloadSems);
        FlushPerNode(_perNodeUploadRates);
        FlushPerNode(_perNodeDownloadRates);
    }

    private static void FlushPerNode<T>(ConcurrentDictionary<string, T> dict) where T : IDisposable
    {
        foreach (var kv in dict.ToArray())
        {
            if (dict.TryRemove(kv.Key, out var stale)) stale.Dispose();
        }
    }

    private static void ReplaceSemaphore(ref SemaphoreSlim? slot, int cap)
    {
        slot?.Dispose();
        slot = cap > 0 ? new SemaphoreSlim(cap, cap) : null;
    }

    private static void ReplaceRateLimiter(ref RateLimiter? slot, int megabytesPerSecond)
    {
        slot?.Dispose();
        slot = megabytesPerSecond > 0 ? BuildBucket(megabytesPerSecond) : null;
    }

    private static RateLimiter BuildBucket(int megabytesPerSecond)
    {
        long bytesPerSecond = (long)megabytesPerSecond * 1024 * 1024;
        // Bucket capacity = 1 second of headroom. TokensPerPeriod = bytesPerSecond/10
        // so the bucket refills the full second over 10 ticks of 100ms.
        long perPeriod = Math.Max(1, bytesPerSecond / (1000 / ReplenishMs));
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
        var globalSem = _globalUploadSem;
        var perNodeSem = GetPerNodeSemaphore(_perNodeUploadSems, nodeId, _snapshot.MaxConcurrentUploadsPerNode);

        if (globalSem != null) await globalSem.WaitAsync(ct).ConfigureAwait(false);
        if (perNodeSem != null)
        {
            try { await perNodeSem.WaitAsync(ct).ConfigureAwait(false); }
            catch { globalSem?.Release(); throw; }
        }

        return new ReleaseHandle(() =>
        {
            // Live setting changes dispose the old semaphores on replace.
            // Swallowing ObjectDisposedException is correct because a
            // disposed semaphore implicitly drops its accounting — there's
            // nothing to "release" once the limiter is gone.
            SafeRelease(perNodeSem);
            SafeRelease(globalSem);
        });
    }

    /// <summary> Symmetric with <see cref="AcquireUploadAsync"/> for downloads. </summary>
    public async Task<IAsyncDisposable> AcquireDownloadAsync(string nodeId, CancellationToken ct)
    {
        var globalSem = _globalDownloadSem;
        var perNodeSem = GetPerNodeSemaphore(_perNodeDownloadSems, nodeId, _snapshot.MaxConcurrentDownloadsPerNode);

        if (globalSem != null) await globalSem.WaitAsync(ct).ConfigureAwait(false);
        if (perNodeSem != null)
        {
            try { await perNodeSem.WaitAsync(ct).ConfigureAwait(false); }
            catch { SafeRelease(globalSem); throw; }
        }

        return new ReleaseHandle(() =>
        {
            SafeRelease(perNodeSem);
            SafeRelease(globalSem);
        });
    }

    private static void SafeRelease(SemaphoreSlim? sem)
    {
        if (sem == null) return;
        try { sem.Release(); }
        catch (ObjectDisposedException) { /* limiter was rebuilt under us; the slot is implicitly freed */ }
        catch (SemaphoreFullException)  { /* already at max — defensive, shouldn't happen with our pairing */ }
    }

    /// <summary>
    ///     Awaits enough tokens to send <paramref name="bytes"/> through the
    ///     upload bandwidth bucket. Acquires from cluster-wide first, then
    ///     per-node — slowest cap wins. Returns immediately when both caps
    ///     are unlimited.
    /// </summary>
    public async Task AcquireUploadBandwidthAsync(string nodeId, int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;

        var global = _globalUploadRate;
        var perNode = GetPerNodeRateLimiter(_perNodeUploadRates, nodeId, _snapshot.MaxUploadMBpsPerNode);

        if (global != null)
        {
            using var lease = await global.AcquireAsync(bytes, ct).ConfigureAwait(false);
            // No body — leasing IS the wait; lease disposes immediately.
        }
        if (perNode != null)
        {
            using var lease = await perNode.AcquireAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary> Symmetric with <see cref="AcquireUploadBandwidthAsync"/> for downloads. </summary>
    public async Task AcquireDownloadBandwidthAsync(string nodeId, int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;

        var global = _globalDownloadRate;
        var perNode = GetPerNodeRateLimiter(_perNodeDownloadRates, nodeId, _snapshot.MaxDownloadMBpsPerNode);

        if (global != null)
        {
            using var lease = await global.AcquireAsync(bytes, ct).ConfigureAwait(false);
        }
        if (perNode != null)
        {
            using var lease = await perNode.AcquireAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Drops the per-node limiters for a removed node. Called from
    ///     <c>ClusterService</c> when a node disconnects permanently.
    /// </summary>
    public void ForgetNode(string nodeId)
    {
        if (_perNodeUploadSems.TryRemove(nodeId, out var s1)) s1.Dispose();
        if (_perNodeDownloadSems.TryRemove(nodeId, out var s2)) s2.Dispose();
        if (_perNodeUploadRates.TryRemove(nodeId, out var r1)) r1.Dispose();
        if (_perNodeDownloadRates.TryRemove(nodeId, out var r2)) r2.Dispose();
    }

    private static SemaphoreSlim? GetPerNodeSemaphore(
        ConcurrentDictionary<string, SemaphoreSlim> dict, string nodeId, int cap)
    {
        if (cap <= 0) return null;
        return dict.GetOrAdd(nodeId, _ => new SemaphoreSlim(cap, cap));
    }

    private static RateLimiter? GetPerNodeRateLimiter(
        ConcurrentDictionary<string, RateLimiter> dict, string nodeId, int megabytesPerSecond)
    {
        if (megabytesPerSecond <= 0) return null;
        return dict.GetOrAdd(nodeId, _ => BuildBucket(megabytesPerSecond));
    }

    /// <summary>
    ///     The chunk size the master should use for uploads, in bytes. Lives
    ///     here so callers don't need to read settings directly.
    /// </summary>
    public int ChunkSizeBytes => Math.Clamp(_snapshot.ChunkSizeMB, 4, 256) * 1024 * 1024;

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _globalUploadSem?.Dispose();
        _globalDownloadSem?.Dispose();
        _globalUploadRate?.Dispose();
        _globalDownloadRate?.Dispose();
        FlushPerNode(_perNodeUploadSems);
        FlushPerNode(_perNodeDownloadSems);
        FlushPerNode(_perNodeUploadRates);
        FlushPerNode(_perNodeDownloadRates);
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
