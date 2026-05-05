namespace Snacks.Services.Slots;

/// <summary>
///     The lifecycle phase of a slot reservation, owned by <see cref="SlotLedger"/>.
///     Distinct from the wire-format phase strings ("Receiving", "Encoding",
///     "Downloading") that the dashboard and worker heartbeats use — see
///     <see cref="SlotPhaseExtensions.ToWireString"/> for the mapping.
/// </summary>
public enum SlotPhase
{
    /// <summary>
    ///     Reservation created by the dispatcher; transfer hasn't started yet.
    ///     The slot is held but no bytes are flowing.
    /// </summary>
    Reserved,

    /// <summary> Master is uploading the source file to the worker. </summary>
    Uploading,

    /// <summary> Worker is encoding. The worker's heartbeat is authoritative for progress. </summary>
    Encoding,

    /// <summary> Master is downloading the encoded output back from the worker. </summary>
    Downloading,

    /// <summary>
    ///     Terminal-state cleanup is in flight (saving output, updating DB,
    ///     broadcasting). The reservation is about to be released; tracked as
    ///     a distinct phase so heartbeat reconcile doesn't re-attach to a
    ///     job whose worker entry has already been dismissed.
    /// </summary>
    Completing
}

/// <summary>
///     Maps <see cref="SlotPhase"/> values to and from the wire-format phase
///     strings reported by worker heartbeats and consumed by the dashboard.
/// </summary>
public static class SlotPhaseExtensions
{
    /// <summary> Wire-format string the worker heartbeat / dashboard expects. </summary>
    public static string ToWireString(this SlotPhase phase) => phase switch
    {
        SlotPhase.Reserved    => "Uploading",   // pre-transfer: surface as Uploading until bytes flow
        SlotPhase.Uploading   => "Uploading",
        SlotPhase.Encoding    => "Encoding",
        SlotPhase.Downloading => "Downloading",
        SlotPhase.Completing  => "Downloading",
        _                     => "Uploading",
    };

    /// <summary>
    ///     Best-effort parse from the worker's wire string. Unknown values map
    ///     to <see cref="SlotPhase.Encoding"/> — the worker only ever reports
    ///     phases for slots that are past the upload handover, so Encoding is
    ///     the safe default.
    /// </summary>
    public static SlotPhase FromWireString(string? wire) => wire switch
    {
        "Receiving"   => SlotPhase.Uploading,
        "Uploading"   => SlotPhase.Uploading,
        "Encoding"    => SlotPhase.Encoding,
        "Downloading" => SlotPhase.Downloading,
        _             => SlotPhase.Encoding,
    };
}
