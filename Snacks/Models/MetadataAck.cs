namespace Snacks.Models;

/// <summary>
///     Reply from a worker node to the master after metadata registration.
///     Tells the master whether this job will run in shared-storage mode (skip
///     upload, read source directly from the shared mount) or fall back to the
///     regular chunk-upload flow. Older nodes that don't know the field reply with
///     a plain <c>{ registered: true }</c>; the master treats that as <c>"upload"</c>.
/// </summary>
public sealed class MetadataAck
{
    /// <summary>
    ///     <c>"shared"</c> when the node accepted shared-storage mode and the
    ///     master should skip <c>UploadFileToNodeAsync</c>; <c>"upload"</c>
    ///     otherwise (the default and the back-compat answer).
    /// </summary>
    public string Mode { get; set; } = "upload";

    /// <summary>
    ///     Optional human-readable explanation for shared-mode rejection
    ///     (e.g. "input path not under allowlist"). Logged on the master so an
    ///     operator can diagnose silent fallbacks.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    ///     The path the node resolved for the shared input (after rewrite +
    ///     symlink resolution). Echoed back for diagnostic logging only — the
    ///     master continues to track its own view of the path.
    /// </summary>
    public string? ResolvedInputPath { get; set; }

    /// <summary>
    ///     The path the node resolved for the shared output. Echoed back for
    ///     diagnostic logging only.
    /// </summary>
    public string? ResolvedOutputPath { get; set; }
}
