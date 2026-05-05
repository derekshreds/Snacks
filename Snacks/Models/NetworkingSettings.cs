namespace Snacks.Models;

/// <summary>
///     Master-only configuration for cluster transfer concurrency and
///     bandwidth. Loaded by <c>NetworkingSettingsService</c> from
///     <c>config/networking.json</c> and consumed by the transfer throttle
///     before each upload/download chunk.
///
///     <para>All caps default to <c>0</c> meaning "unlimited" so an empty
///     config preserves the unrestricted behaviour shipped before this
///     feature. The single intentional behaviour change is the per-node
///     upload concurrency default of <c>1</c>: that's the cap that
///     directly addresses the user-reported network saturation.</para>
/// </summary>
public sealed class NetworkingSettings
{
    /// <summary>
    ///     Maximum concurrent uploads cluster-wide. <c>0</c> = unlimited.
    ///     Gated by a global <see cref="System.Threading.SemaphoreSlim"/>.
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 0;

    /// <summary>
    ///     Maximum concurrent uploads per receiving node. Defaults to
    ///     <c>1</c> so a node with multiple device slots still receives one
    ///     file at a time — the natural fix for "3 uploads in flight on
    ///     hugin saturating my switch."
    /// </summary>
    public int MaxConcurrentUploadsPerNode { get; set; } = 1;

    /// <summary> Maximum concurrent downloads cluster-wide. <c>0</c> = unlimited. </summary>
    public int MaxConcurrentDownloads { get; set; } = 0;

    /// <summary> Maximum concurrent downloads per source node. <c>0</c> = unlimited. </summary>
    public int MaxConcurrentDownloadsPerNode { get; set; } = 1;

    /// <summary>
    ///     Cluster-wide cap on upload bandwidth in megabytes per second. <c>0</c>
    ///     = unlimited. Implemented as a token-bucket rate limiter; chunks
    ///     consume tokens equal to their byte count before transmitting.
    /// </summary>
    public int MaxUploadMBps { get; set; } = 0;

    /// <summary> Per-node cap on upload bandwidth in MB/s. <c>0</c> = unlimited. </summary>
    public int MaxUploadMBpsPerNode { get; set; } = 0;

    /// <summary> Cluster-wide cap on download bandwidth in MB/s. <c>0</c> = unlimited. </summary>
    public int MaxDownloadMBps { get; set; } = 0;

    /// <summary> Per-node cap on download bandwidth in MB/s. <c>0</c> = unlimited. </summary>
    public int MaxDownloadMBpsPerNode { get; set; } = 0;

    /// <summary>
    ///     Chunk size in megabytes for the chunked upload/download protocol.
    ///     Clamped to the 4-256 MB range. Smaller chunks improve rate-limit
    ///     smoothness at the cost of more HTTP overhead per file; the default
    ///     of 50 matches the historical hard-coded value.
    /// </summary>
    public int ChunkSizeMB { get; set; } = 50;
}
