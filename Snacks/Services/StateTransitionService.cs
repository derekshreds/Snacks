namespace Snacks.Services;

using Snacks.Data;
using Snacks.Models;

/// <summary>
///     Wraps all job phase changes with write-ahead logging via the <see cref="StateTransition"/> table.
///     Every transition is recorded BEFORE the actual state change occurs, creating an audit trail
///     that survives crashes and restarts.
///
///     <para>Usage pattern:</para>
///     <code>
///     await using var scope = await _stateTransitions.BeginAsync(workItemId, "Queued", "Uploading");
///     // ... perform the actual state change ...
///     await scope.CompleteAsync();
///     </code>
///
///     <para>During recovery, incomplete transitions (Completed = false) indicate interrupted
///     operations that need to be replayed or rolled back.</para>
/// </summary>
public sealed class StateTransitionService
{
    private readonly MediaFileRepository _repo;

    /// <summary> Creates a new state transition service using the specified repository. </summary>
    public StateTransitionService(MediaFileRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    ///     Begins a new state transition by persisting a write-ahead log entry.
    ///     Call <see cref="TransitionScope.CompleteAsync"/> after the actual state change succeeds.
    /// </summary>
    /// <param name="workItemId"> The job ID this transition applies to. </param>
    /// <param name="fromPhase"> The current phase; null for initial assignments. </param>
    /// <param name="toPhase"> The target phase. </param>
    /// <returns> A scope that must be completed to mark the transition as finished. </returns>
    public async Task<TransitionScope> BeginAsync(string workItemId, string? fromPhase, string toPhase)
    {
        var transitionId = await _repo.BeginTransitionAsync(workItemId, fromPhase ?? "", toPhase);
        return new TransitionScope(_repo, transitionId, workItemId, fromPhase, toPhase);
    }

    /// <summary>
    ///     Returns all incomplete transitions for recovery processing.
    /// </summary>
    public async Task<List<StateTransition>> GetIncompleteTransitionsAsync()
    {
        return await _repo.GetIncompleteTransitionsAsync();
    }

    /// <summary>
    ///     A disposable scope representing an in-progress state transition.
    ///     Must be completed explicitly via <see cref="CompleteAsync"/>.
    /// </summary>
    public sealed class TransitionScope
    {
        private readonly MediaFileRepository _repo;
        private readonly int _transitionId;

        internal TransitionScope(MediaFileRepository repo, int transitionId, string workItemId, string? fromPhase, string toPhase)
        {
            _repo = repo;
            _transitionId = transitionId;
            WorkItemId = workItemId;
            FromPhase = fromPhase;
            ToPhase = toPhase;
        }

        /// <summary> The job ID this transition applies to. </summary>
        public string WorkItemId { get; }

        /// <summary> The phase this transition is moving from. </summary>
        public string? FromPhase { get; }

        /// <summary> The phase this transition is moving to. </summary>
        public string ToPhase { get; }

        /// <summary>
        ///     Marks the transition as completed. Must be called AFTER the actual
        ///     state change has been applied successfully.
        /// </summary>
        public async Task CompleteAsync()
        {
            await _repo.CompleteTransitionAsync(_transitionId);
        }
    }
}
