namespace Snacks.Models;

/// <summary>
///     Tracks a completed job on a worker node that hasn't been acknowledged by the master yet.
///     Persisted to disk (pending-completions.json) so it survives node restarts.
///
///     When a node finishes encoding, it retries the completion POST up to 10 times.
///     If all retries fail (e.g., master is down), the completion is persisted here
///     and retried on every subsequent heartbeat until the master acknowledges receipt.
///
///     This ensures that completed output files are never lost due to transient
///     network failures or master downtime.
/// </summary>
public sealed class PendingCompletion
{
    /// <summary> The job ID (WorkItem.Id) that completed on this node. </summary>
    public string JobId { get; set; } = "";

    /// <summary>
    ///     The master's URL at the time of completion.
    ///     Used to retry the completion POST on heartbeat.
    /// </summary>
    public string MasterUrl { get; set; } = "";

    /// <summary>
    ///     Name of the output file (e.g., "Movie [snacks].mkv").
    ///     Included in the completion POST for master-side tracking.
    /// </summary>
    public string OutputFileName { get; set; } = "";

    /// <summary>
    ///     UTC timestamp when the job completed.
    ///     Used for diagnostics and potential TTL-based cleanup.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
