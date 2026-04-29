namespace Snacks.Models;

/// <summary>
///     Persistent record of a single successful encode. Written once per
///     completed (or no-savings) job, on either the master's local scheduler
///     or after a remote node hands its output back to the master. Powers
///     the analytics dashboard — savings totals, per-device utilization,
///     codec mix, recent-encodes table, and the various "your library got
///     smarter" stats.
///
///     <para>This record is intentionally additive. It does not replace
///     <see cref="MediaFile"/>'s lifecycle status; <see cref="MediaFile"/>
///     remains the source of truth for "what's the current state of this
///     file on disk", while <see cref="EncodeHistory"/> is an append-only
///     ledger of "what work has already been done".</para>
/// </summary>
public sealed class EncodeHistory
{
    /// <summary> Auto-increment primary key. </summary>
    public int Id { get; set; }

    /// <summary>
    ///     The <see cref="WorkItem.Id"/> at the time the encode finished.
    ///     Useful for cross-referencing with logs that survive past the
    ///     ephemeral work-item lifetime.
    /// </summary>
    public string JobId { get; set; } = "";

    /// <summary> Absolute, normalized path of the source file at encode time. </summary>
    public string FilePath { get; set; } = "";

    /// <summary> File name with extension (display-friendly). </summary>
    public string FileName { get; set; } = "";

    /// <summary> Source file size in bytes prior to the encode. </summary>
    public long OriginalSizeBytes { get; set; }

    /// <summary>
    ///     Output file size in bytes. <c>0</c> when the encode produced no
    ///     savings and the output was discarded — these rows still get
    ///     persisted so "encode hours" and "device utilization" reflect the
    ///     work done, but they don't contribute to the savings totals.
    /// </summary>
    public long EncodedSizeBytes { get; set; }

    /// <summary>
    ///     Convenience-computed savings (<c>OriginalSizeBytes - EncodedSizeBytes</c>),
    ///     persisted directly so dashboard aggregations don't need to recompute on
    ///     every query. Negative values are clamped to 0 by the writer.
    /// </summary>
    public long BytesSaved { get; set; }

    /// <summary> Source video codec (e.g. <c>"h264"</c>, <c>"hevc"</c>). </summary>
    public string OriginalCodec { get; set; } = "";

    /// <summary> Output video codec. </summary>
    public string EncodedCodec { get; set; } = "";

    /// <summary> Source video bitrate in kbps. </summary>
    public long OriginalBitrateKbps { get; set; }

    /// <summary> Output video bitrate in kbps. </summary>
    public long EncodedBitrateKbps { get; set; }

    /// <summary> Source video duration in seconds (content time, not encode time). </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    ///     Wall-clock time spent encoding, in seconds. Powers the
    ///     "device utilization" stripe and the speed comparison
    ///     (<c>DurationSeconds / EncodeSeconds</c> = realtime multiplier).
    /// </summary>
    public double EncodeSeconds { get; set; }

    /// <summary>
    ///     The <see cref="HardwareDevice.DeviceId"/> the encode actually ran
    ///     on (<c>"nvidia"</c>, <c>"intel"</c>, <c>"amd"</c>, <c>"apple"</c>,
    ///     or <c>"cpu"</c>). Drives the per-device dashboard charts.
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    ///     The <see cref="ClusterNode.NodeId"/> that ran the encode, or the
    ///     master's own node id for local encodes. Lets the dashboard show
    ///     per-node throughput leaderboards.
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary> Hostname of the encoding node, snapshotted for display. </summary>
    public string NodeHostname { get; set; } = "";

    /// <summary> Whether this encode ran in cluster (remote) mode or locally on the master. </summary>
    public bool WasRemote { get; set; }

    /// <summary> Whether the source was 4K — useful for filtering the dashboard to UHD wins. </summary>
    public bool Is4K { get; set; }

    /// <summary> UTC timestamp when encoding started. </summary>
    public DateTime StartedAt { get; set; }

    /// <summary> UTC timestamp when encoding finished and the row was written. </summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>
    ///     Outcome marker. Values: <c>"Completed"</c> (output kept) or
    ///     <c>"NoSavings"</c> (output discarded because it wasn't smaller).
    ///     Failed encodes are <em>not</em> recorded here — the dashboard is
    ///     a ledger of completed work, not an error log.
    /// </summary>
    public string Outcome { get; set; } = "Completed";
}
