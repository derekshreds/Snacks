namespace Snacks.Services;

using Snacks.Models;

/// <summary>
///     Per-entry decision logic for the master's heartbeat reconciliation of
///     a node's optimistic <see cref="ClusterNode.ActiveJobs"/> list. Pulled
///     out as a pure static so the slot-preserve rules can be unit-tested
///     directly without spinning up <see cref="ClusterService"/>.
///
///     <para>An <see cref="ActiveJobInfo"/> entry must be preserved while any
///     master-side or worker-side state still owns the slot — uploading on
///     the master, encoding on the worker, downloading on the master, or
///     simply registered in <c>_remoteJobs</c> for this node (covers the
///     upload→encode handover gap and any other transient state where
///     neither side has reported the job yet but the master still considers
///     it assigned).</para>
/// </summary>
public static class SlotReconciler
{
    /// <summary>
    ///     Decides whether a single <see cref="ClusterNode.ActiveJobs"/> entry
    ///     should be kept during heartbeat merge, and which phase string to
    ///     stamp onto the kept entry.
    /// </summary>
    /// <param name="entry">The optimistic slot reservation under consideration.</param>
    /// <param name="reportedActive">Jobs the worker reports actively encoding.</param>
    /// <param name="activeUploads">Master-side upload claims keyed by jobId.</param>
    /// <param name="activeDownloads">Master-side download claims keyed by jobId.</param>
    /// <param name="completedIds">Jobs the worker has finished encoding but master has not yet pulled.</param>
    /// <param name="remoteJobs">Master's authoritative remote-job map, keyed by jobId.</param>
    /// <param name="nodeId">The node whose <see cref="ClusterNode.ActiveJobs"/> is being reconciled.</param>
    /// <returns>
    ///     <c>Keep</c> = whether the entry survives the merge.
    ///     <c>PhaseOverride</c> = phase string to assign (<c>"Uploading"</c>,
    ///     <c>"Downloading"</c>, or <c>null</c> meaning "leave the entry's
    ///     existing phase alone — the worker's report wins").
    /// </returns>
    public static (bool Keep, string? PhaseOverride) ShouldPreserveEntry(
        ActiveJobInfo entry,
        IReadOnlyCollection<ActiveJobInfo> reportedActive,
        IReadOnlyDictionary<string, bool> activeUploads,
        IReadOnlyDictionary<string, bool> activeDownloads,
        IReadOnlySet<string> completedIds,
        IReadOnlyDictionary<string, WorkItem> remoteJobs,
        string nodeId)
    {
        // Worker is encoding this job — its report wins (caller mutates
        // the entry's Progress/Phase from the worker's payload).
        if (reportedActive.Any(r => r.JobId == entry.JobId))
            return (true, null);

        // Master is uploading the file — slot held, phase forced to Uploading.
        if (activeUploads.ContainsKey(entry.JobId))
            return (true, "Uploading");

        // Master still considers this job assigned to this node — covers the
        // upload→encode handover gap (upload finished, worker hasn't reported
        // encoding yet) and any other transient state. _remoteJobs is removed
        // in every terminal path, so membership alone is the "still ours" signal.
        if (remoteJobs.TryGetValue(entry.JobId, out var rj) && rj.AssignedNodeId == nodeId)
            return (true, null);

        // Master is downloading the encoded output, OR worker has signalled
        // completion and master is about to start the download. Either way
        // the slot is still consumed by this job.
        if (activeDownloads.ContainsKey(entry.JobId) || completedIds.Contains(entry.JobId))
            return (true, "Downloading");

        // Stale: neither side owns this job anymore.
        return (false, null);
    }
}
