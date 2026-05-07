using Snacks.Models;

namespace Snacks.Services.Routing;

/// <summary>
///     Per-kind dispatch strategy. Music and video flow through enough
///     kind-specific code (synthetic vs. hardware devices, separate skip
///     ladders, separate encoder methods, separate hwaccel pinning) that
///     a conditional-per-site implementation drifted out of sync as music
///     was bolted onto an originally video-only system. One implementation
///     per <see cref="MediaKind"/> keeps every kind-specific decision
///     in one file — adding a new job kind is a single new router class
///     plus a DI registration, with no scattered <c>if (kind == ...)</c>
///     branches in the dispatch / encode / scoring paths.
/// </summary>
public interface IJobKindRouter
{
    /// <summary> The job kind this router owns. Used as the registry key. </summary>
    MediaKind Kind { get; }

    /// <summary>
    ///     Synthetic device id this kind exclusively targets, or <see langword="null"/>
    ///     when the kind is dispatched to real hardware devices. Music's
    ///     <c>"music"</c> isn't part of any worker's hardware probe — the
    ///     master's slot allocator and the worker's
    ///     <c>ClusterNodeJobService.ResolveDeviceId</c> both need to
    ///     recognise it without consulting the hardware list.
    /// </summary>
    string? SyntheticDeviceId { get; }

    /// <summary>
    ///     Pins remote-encoder options for a worker dispatch. Video maps the
    ///     master-selected device family onto <see cref="EncoderOptions.HardwareAcceleration"/>
    ///     and <see cref="EncoderOptions.HardwareDevicePath"/>; music ignores
    ///     both fields entirely (passing <c>-hwaccel music</c> crashes ffmpeg).
    /// </summary>
    /// <param name="options">Encoder options to mutate in place.</param>
    /// <param name="deviceId">The device the master allocated for this dispatch.</param>
    /// <param name="devicePathResolver">Resolves a deviceId to its node-local <c>/dev</c> path, or null for cpu / synthetic.</param>
    void PinRemoteEncoderOptions(EncoderOptions options, string deviceId, Func<string, string?> devicePathResolver);

    /// <summary>
    ///     Scores a (node, device) slot for an item of this kind. Returns
    ///     ≤ 0 when the slot can't host this kind, positive when it can
    ///     (higher = better). The dispatch loop picks the highest-scoring
    ///     slot across the whole cluster.
    /// </summary>
    int ScoreSlot(ClusterNode node, HardwareDevice device, WorkItem item, EncoderOptions options, ScoreContext ctx);

    /// <summary>
    ///     Runs the actual encode on the worker side after the master has
    ///     uploaded the source. Each kind delegates to a different
    ///     <see cref="TranscodingService"/> entry point.
    /// </summary>
    Task EncodeRemoteAsync(WorkItem item, EncoderOptions options, CancellationToken ct);

    /// <summary>
    ///     The file extension (with leading dot) the worker's encoder will
    ///     produce for an item of this kind under <paramref name="options"/>.
    ///     The master uses this to compute the local download path so the
    ///     bytes streamed back from the worker are written to a file with
    ///     the correct extension. Without per-kind dispatch the cluster
    ///     completion path would force a video extension (<c>.mkv</c>/<c>.mp4</c>)
    ///     onto every output and music encodes would land as
    ///     <c>"track [snacks].mkv"</c> — playable as audio but cosmetically
    ///     wrong, and confusing for downstream consumers that key on
    ///     extension.
    /// </summary>
    string ExpectedOutputExtension(EncoderOptions options);
}

/// <summary>
///     Read-only view of the data routers need to score a slot, passed in by
///     the dispatcher rather than letting routers reach into <see cref="ClusterService"/>.
///     Keeps the router surface narrow and testable.
/// </summary>
public sealed class ScoreContext
{
    /// <summary> True if the device is enabled by user node-settings. </summary>
    public required Func<ClusterNode, string, bool> IsDeviceEnabled { get; init; }

    /// <summary> Live count of reservations on (node, device). </summary>
    public required Func<ClusterNode, string, int> UsedDeviceSlots { get; init; }

    /// <summary> Effective per-device capacity for (node, device), honouring overrides. </summary>
    public required Func<ClusterNode, HardwareDevice, int> EffectiveDeviceCapacity { get; init; }

    /// <summary> Resolves a node's persisted <see cref="NodeSettings"/>, or null if none. </summary>
    public required Func<string, NodeSettings?> GetNodeSettings { get; init; }
}
