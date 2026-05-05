namespace Snacks.Services.Slots;

/// <summary>
///     A single per-slot reservation owned by <see cref="SlotLedger"/>. The
///     ledger is the authoritative store; the master's per-node
///     <c>ClusterNode.ActiveJobs</c> field is a read-only projection of the
///     subset of reservations belonging to that node, materialised on demand
///     by <see cref="SlotLedger.Snapshot"/>.
///
///     <para>Mutable fields (Phase, Progress, FileName, PhaseEnteredAt) are
///     only modified through ledger methods that hold its write-lock.</para>
/// </summary>
public sealed class SlotReservation
{
    /// <summary> The work item ID. Immutable identity of the reservation. </summary>
    public required string JobId { get; init; }

    /// <summary> NodeId of the worker that owns this slot. Immutable for the reservation's lifetime. </summary>
    public required string NodeId { get; init; }

    /// <summary>
    ///     The <c>HardwareDevice.DeviceId</c> the master allocated. Immutable —
    ///     a job cannot move between devices without releasing first.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary> File name for display in the queue UI; may be null on early-recovery rows. </summary>
    public string? FileName { get; set; }

    /// <summary> Lifecycle phase. Mutated only through <see cref="SlotLedger.TransitionPhase"/>. </summary>
    public SlotPhase Phase { get; set; }

    /// <summary> 0–100. Mutated by <see cref="SlotLedger.UpdateProgress"/> from worker heartbeats. </summary>
    public int Progress { get; set; }

    /// <summary> When the reservation was created. </summary>
    public required DateTime ReservedAt { get; init; }

    /// <summary> When the reservation last changed phase. Used for stuck-job diagnostics. </summary>
    public DateTime PhaseEnteredAt { get; set; }
}
