using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     Persistent configuration for the auto-scan background service.
///     Serialized to autoscan.json in the config directory.
/// </summary>
public sealed class AutoScanConfig
{
    /// <summary> Whether automatic directory scanning is enabled. </summary>
    public bool Enabled { get; set; } = false;

    /// <summary> How often to run a scan, in minutes. Default: 60. </summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    ///     Watched directories with optional per-folder encoding overrides.
    ///     Backward-compatible: legacy string arrays are auto-promoted to <see cref="WatchedFolder"/> on load.
    /// </summary>
    [JsonConverter(typeof(WatchedFolderListConverter))]
    public List<WatchedFolder> Directories { get; set; } = new();

    /// <summary> UTC timestamp of the most recent completed scan. Null if never scanned. </summary>
    public DateTime? LastScanTime { get; set; }

    /// <summary> Number of new files found during the last scan. </summary>
    public int LastScanNewFiles { get; set; } = 0;

    /// <summary>
    ///     Whether the encoding queue is paused. Persisted so the paused state
    ///     survives application restarts.
    /// </summary>
    public bool QueuePaused { get; set; } = false;
}
