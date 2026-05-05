using System.Collections.Concurrent;
using Snacks.Models;

namespace Snacks.Services.Slots;

/// <summary>
///     Single authoritative store for every per-slot reservation in the
///     cluster. Replaces the historical scheme where slot truth was scattered
///     across <c>ClusterNode.ActiveJobs</c>, <c>_activeUploads</c>,
///     <c>_activeDownloads</c>, <c>_remoteJobs</c>, and worker heartbeats —
///     any one of which could silently lose an entry and cause double-dispatch.
///
///     <para>Contract:</para>
///     <list type="bullet">
///         <item><description>A reservation is created exactly once via <see cref="TryReserve"/>.</description></item>
///         <item><description>It is removed exactly once via <see cref="Release"/>.</description></item>
///         <item><description>Heartbeat reconcile mutates only Phase / Progress through <see cref="UpdateProgress"/>; it never deletes.</description></item>
///         <item><description><see cref="Snapshot"/> returns a wire-compatible <see cref="ActiveJobInfo"/> list for serialisation.</description></item>
///     </list>
///
///     <para>Thread safety: all public mutators are protected by an internal
///     write-lock so the read-then-write atomicity of <see cref="TryReserve"/>
///     (capacity check + insert) is preserved under contention.</para>
/// </summary>
public sealed class SlotLedger
{
    private readonly ConcurrentDictionary<string, SlotReservation> _reservations = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    /// <summary>
    ///     Resolves the maximum concurrent jobs allowed on a (nodeId, deviceId)
    ///     slot. Injected by <c>ClusterService</c> so the ledger doesn't have
    ///     to know about NodeSettings or HardwareDevice; the resolver consults
    ///     the user's per-device override and the worker's reported default.
    /// </summary>
    private readonly Func<string, string, int> _capacityResolver;

    private readonly Action<string>? _logger;

    /// <param name="capacityResolver">
    ///     <c>(nodeId, deviceId) =&gt; capacity</c>. Should return 0 for
    ///     unknown / disabled / not-yet-reported devices — <see cref="TryReserve"/>
    ///     will refuse to reserve in that case.
    /// </param>
    /// <param name="logger">Optional sink for release-reason logging. Defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public SlotLedger(Func<string, string, int> capacityResolver, Action<string>? logger = null)
    {
        _capacityResolver = capacityResolver ?? throw new ArgumentNullException(nameof(capacityResolver));
        _logger = logger;
    }

    /// <summary>
    ///     Atomically check capacity and create the reservation. Returns
    ///     <see langword="false"/> when the device is at capacity, when the
    ///     resolver returns 0 (device disabled or unknown), or when a
    ///     reservation for this <paramref name="jobId"/> already exists.
    ///
    ///     <para>Phase starts at <see cref="SlotPhase.Reserved"/>. The dispatch
    ///     path normally calls <see cref="TransitionPhase"/> to move to
    ///     <see cref="SlotPhase.Uploading"/> when bytes start flowing.</para>
    /// </summary>
    public bool TryReserve(string nodeId, string deviceId, string jobId, string? fileName)
    {
        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(jobId))
            return false;

        lock (_writeLock)
        {
            if (_reservations.ContainsKey(jobId)) return false;

            int capacity = _capacityResolver(nodeId, deviceId);
            if (capacity <= 0) return false;

            int used = CountUsedUnlocked(nodeId, deviceId);
            if (used >= capacity) return false;

            var now = DateTime.UtcNow;
            var reservation = new SlotReservation
            {
                JobId          = jobId,
                NodeId         = nodeId,
                DeviceId       = deviceId,
                FileName       = fileName,
                Phase          = SlotPhase.Reserved,
                Progress       = 0,
                ReservedAt     = now,
                PhaseEnteredAt = now,
            };
            _reservations[jobId] = reservation;
            return true;
        }
    }

    /// <summary>
    ///     Idempotent. Removes the reservation and emits a single log line
    ///     attributing the release. Calling for an unknown <paramref name="jobId"/>
    ///     is a no-op — failure / completion paths often fire from multiple
    ///     handlers and this lets them stay simple.
    /// </summary>
    public void Release(string jobId, ReleaseReason reason)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        SlotReservation? removed;
        lock (_writeLock)
        {
            _reservations.TryRemove(jobId, out removed);
        }

        if (removed == null) return;

        var msg = $"SlotLedger: Released {jobId} on {removed.NodeId}/{removed.DeviceId} (phase={removed.Phase}, reason={reason})";
        if (_logger != null) _logger(msg);
        else Console.WriteLine(msg);
    }

    /// <summary>
    ///     Move an existing reservation to <paramref name="next"/>. No-op when
    ///     the reservation doesn't exist (already released) or is already in
    ///     the requested phase (avoids resetting <see cref="SlotReservation.PhaseEnteredAt"/>
    ///     on duplicate transitions).
    /// </summary>
    public void TransitionPhase(string jobId, SlotPhase next)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        lock (_writeLock)
        {
            if (!_reservations.TryGetValue(jobId, out var r)) return;
            if (r.Phase == next) return;
            r.Phase = next;
            r.PhaseEnteredAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Heartbeat hook: stamp the worker-reported progress / phase onto the
    ///     existing reservation. Never creates or deletes; if the worker is
    ///     reporting a job the master doesn't know about, that's an anomaly
    ///     handled separately by the caller.
    /// </summary>
    public void UpdateProgress(string jobId, int progress, string? phaseHint)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        lock (_writeLock)
        {
            if (!_reservations.TryGetValue(jobId, out var r)) return;
            r.Progress = progress;
            if (!string.IsNullOrEmpty(phaseHint))
            {
                var inferred = SlotPhaseExtensions.FromWireString(phaseHint);
                if (inferred != r.Phase)
                {
                    r.Phase = inferred;
                    r.PhaseEnteredAt = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>
    ///     Counts active reservations on the given (node, device) slot.
    ///     Used by the dispatch path to decide if another job will fit.
    /// </summary>
    public int UsedDeviceSlots(string nodeId, string deviceId)
    {
        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(deviceId)) return 0;
        return CountUsedUnlocked(nodeId, deviceId);
    }

    private int CountUsedUnlocked(string nodeId, string deviceId)
    {
        int count = 0;
        foreach (var r in _reservations.Values)
        {
            if (r.NodeId == nodeId && r.DeviceId == deviceId) count++;
        }
        return count;
    }

    /// <summary>
    ///     Materialises a wire-compatible <see cref="ActiveJobInfo"/> list for
    ///     the given node. Called immediately before SignalR broadcasts so the
    ///     dashboard chips see the current ledger state without the heartbeat
    ///     reconcile having to mutate <c>ClusterNode.ActiveJobs</c>.
    /// </summary>
    public List<ActiveJobInfo> Snapshot(string nodeId)
    {
        var list = new List<ActiveJobInfo>();
        if (string.IsNullOrEmpty(nodeId)) return list;
        foreach (var r in _reservations.Values)
        {
            if (r.NodeId != nodeId) continue;
            list.Add(new ActiveJobInfo
            {
                JobId    = r.JobId,
                DeviceId = r.DeviceId,
                FileName = r.FileName,
                Progress = r.Progress,
                Phase    = r.Phase.ToWireString(),
            });
        }
        return list;
    }

    /// <summary> True if a reservation for <paramref name="jobId"/> currently exists. </summary>
    public bool Contains(string jobId) =>
        !string.IsNullOrEmpty(jobId) && _reservations.ContainsKey(jobId);

    /// <summary> Returns the current phase of the reservation, or null if not reserved. </summary>
    public SlotPhase? GetPhase(string jobId) =>
        _reservations.TryGetValue(jobId, out var r) ? r.Phase : null;

    /// <summary> Returns the live reservation row, or null if not reserved. </summary>
    public SlotReservation? GetReservation(string jobId) =>
        _reservations.TryGetValue(jobId, out var r) ? r : null;

    /// <summary> Snapshot of all reservations, safe to enumerate without locking. </summary>
    public IReadOnlyList<SlotReservation> EnumerateAll() =>
        _reservations.Values.ToList();

    /// <summary>
    ///     Counts reservations cluster-wide in the given phase. Used for
    ///     graceful-shutdown drain checks ("are any uploads still going?").
    /// </summary>
    public int CountByPhase(SlotPhase phase)
    {
        int count = 0;
        foreach (var r in _reservations.Values)
            if (r.Phase == phase) count++;
        return count;
    }

    /// <summary>
    ///     Removes every reservation. Used by <c>ClearAllRemoteStateAsync</c>
    ///     during cluster reset / role-change. No release-reason logging —
    ///     this is bulk teardown, not lifecycle.
    /// </summary>
    public void Clear()
    {
        lock (_writeLock) _reservations.Clear();
    }
}
