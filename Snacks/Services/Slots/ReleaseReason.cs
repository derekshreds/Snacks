namespace Snacks.Services.Slots;

/// <summary>
///     Why a <see cref="SlotLedger"/> reservation was released. Logged on every
///     release so the lifecycle of any leaked-or-not slot is auditable from
///     the console output alone.
/// </summary>
public enum ReleaseReason
{
    /// <summary> Job finished successfully and output was kept. </summary>
    Completed,

    /// <summary> Job finished but the encoded output didn't beat the source (no savings, sticky). </summary>
    NoSavings,

    /// <summary> User cancelled the job. </summary>
    Cancelled,

    /// <summary>
    ///     The owning node went offline or was declared failed by the watchdog
    ///     / heartbeat reconcile. Job is re-queued for another node.
    /// </summary>
    NodeFailed,

    /// <summary>
    ///     The dispatch <c>Task.Run</c> body threw before any phase transition
    ///     completed. The pre-claim is being rolled back.
    /// </summary>
    DispatchThrew,

    /// <summary> Source file was missing or failed integrity checks at dispatch time. </summary>
    ValidationFailed,

    /// <summary> Master ran out of download retries pulling the encoded output back. </summary>
    DownloadRetriesExhausted,

    /// <summary>
    ///     One-time release fired during master-restart recovery for a job
    ///     whose worker no longer reports it. Job is re-queued from <c>Pending</c>.
    /// </summary>
    Recovered
}
