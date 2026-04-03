namespace Snacks.Models;

/// <summary>
///     Tracks state transitions for distributed jobs to enable atomic recovery.
///     Each transition is recorded BEFORE the actual state change occurs,
///     creating a write-ahead log that survives crashes and restarts.
///
///     During recovery, incomplete transitions (Completed = false) indicate
///     interrupted operations that need to be replayed or rolled back.
///     This prevents state divergence between the database and in-memory cache.
/// </summary>
public sealed class StateTransition
{
    /// <summary> Auto-incrementing primary key. </summary>
    public int Id { get; set; }

    /// <summary>
    ///     The WorkItem.Id (GUID) this transition applies to.
    ///     Indexed together with <see cref="Completed"/> for fast recovery lookups.
    /// </summary>
    public string WorkItemId { get; set; } = "";

    /// <summary>
    ///     The phase the job was in before this transition (e.g., "Queued").
    ///     Null for initial state assignments.
    /// </summary>
    public string? FromPhase { get; set; }

    /// <summary>
    ///     The target phase (e.g., "Uploading", "Encoding", "Downloading").
    /// </summary>
    public string ToPhase { get; set; } = "";

    /// <summary> UTC timestamp when this transition was initiated. </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Whether the transition completed successfully.
    ///     False indicates an interrupted operation that needs recovery.
    /// </summary>
    public bool Completed { get; set; }
}
