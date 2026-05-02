using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the per-entry preserve rules used by master-side heartbeat
///     reconciliation of <see cref="ClusterNode.ActiveJobs"/>. The rules
///     decide whether an optimistic slot reservation survives the merge
///     against a worker's reported state. Race A (dispatch → upload-task
///     gap) and Race B (upload-finished → worker-reports-encoding gap)
///     both manifest as a preserved entry getting wrongly dropped, which
///     lets the next dispatch tick double-book the slot — the tests
///     beginning with <c>Race</c> are direct regression guards.
/// </summary>
public sealed class SlotReconcilerTests
{
    private const string NodeId = "node-1";

    private static ActiveJobInfo MakeEntry(string jobId, string deviceId = "nvidia", string? phase = null) => new()
    {
        JobId    = jobId,
        DeviceId = deviceId,
        FileName = $"{jobId}.mkv",
        Progress = 0,
        Phase    = phase,
    };

    private static (bool Keep, string? PhaseOverride) Decide(
        ActiveJobInfo entry,
        IReadOnlyCollection<ActiveJobInfo>? reportedActive = null,
        IReadOnlyDictionary<string, bool>? activeUploads = null,
        IReadOnlyDictionary<string, bool>? activeDownloads = null,
        IReadOnlySet<string>? completedIds = null,
        IReadOnlyDictionary<string, WorkItem>? remoteJobs = null,
        string nodeId = NodeId)
    {
        return SlotReconciler.ShouldPreserveEntry(
            entry,
            reportedActive  ?? Array.Empty<ActiveJobInfo>(),
            activeUploads   ?? new Dictionary<string, bool>(),
            activeDownloads ?? new Dictionary<string, bool>(),
            completedIds    ?? new HashSet<string>(),
            remoteJobs      ?? new Dictionary<string, WorkItem>(),
            nodeId);
    }


    [Fact]
    public void Entry_in_worker_reportedActive_is_preserved_with_worker_phase()
    {
        var entry = MakeEntry("job-1");
        var reported = new[] { new ActiveJobInfo { JobId = "job-1", Phase = "Encoding", Progress = 42 } };

        var (keep, phase) = Decide(entry, reportedActive: reported);

        keep.Should().BeTrue();
        phase.Should().BeNull("worker's phase wins — caller copies Phase + Progress from the worker payload");
    }


    [Fact]
    public void Entry_in_active_uploads_is_preserved_with_uploading_phase()
    {
        var entry = MakeEntry("job-1");
        var uploads = new Dictionary<string, bool> { ["job-1"] = true };

        var (keep, phase) = Decide(entry, activeUploads: uploads);

        keep.Should().BeTrue();
        phase.Should().Be("Uploading");
    }


    [Fact]
    public void Entry_in_remote_jobs_for_this_node_is_preserved_during_handover()
    {
        // Race B: upload finished (_activeUploads cleared) but worker has not
        // yet sent the heartbeat showing this job in activeJobs[]. Without
        // _remoteJobs preservation the slot reservation gets dropped and the
        // next dispatch tick double-books the slot.
        var entry = MakeEntry("job-1");
        var workItem = new WorkItem { Id = "job-1", AssignedNodeId = NodeId };
        var remote = new Dictionary<string, WorkItem> { ["job-1"] = workItem };

        var (keep, phase) = Decide(entry, remoteJobs: remote);

        keep.Should().BeTrue();
        phase.Should().BeNull("entry's existing phase is left alone during the handover gap");
    }


    [Fact]
    public void Entry_in_active_downloads_is_preserved_with_downloading_phase()
    {
        var entry = MakeEntry("job-1");
        var downloads = new Dictionary<string, bool> { ["job-1"] = true };

        var (keep, phase) = Decide(entry, activeDownloads: downloads);

        keep.Should().BeTrue();
        phase.Should().Be("Downloading");
    }


    [Fact]
    public void Entry_in_completed_ids_is_preserved_with_downloading_phase()
    {
        var entry = MakeEntry("job-1");
        var completed = new HashSet<string> { "job-1" };

        var (keep, phase) = Decide(entry, completedIds: completed);

        keep.Should().BeTrue();
        phase.Should().Be("Downloading");
    }


    [Fact]
    public void Entry_with_no_owning_state_is_dropped()
    {
        var entry = MakeEntry("orphan-job");

        var (keep, phase) = Decide(entry);

        keep.Should().BeFalse();
        phase.Should().BeNull();
    }


    [Fact]
    public void Entry_in_remote_jobs_for_a_different_node_is_dropped()
    {
        // Cross-node leak guard: a job that's in _remoteJobs but assigned to
        // a different node must not preserve a slot reservation on this node.
        var entry = MakeEntry("job-1");
        var workItem = new WorkItem { Id = "job-1", AssignedNodeId = "some-other-node" };
        var remote = new Dictionary<string, WorkItem> { ["job-1"] = workItem };

        var (keep, phase) = Decide(entry, remoteJobs: remote);

        keep.Should().BeFalse();
        phase.Should().BeNull();
    }


    [Fact]
    public void Entry_in_both_reported_and_uploads_picks_worker_over_uploads()
    {
        // Precedence: worker's report wins over the master's "Uploading"
        // stamp when both states exist (e.g. upload finished and worker
        // started encoding faster than the master cleared _activeUploads).
        var entry = MakeEntry("job-1");
        var reported = new[] { new ActiveJobInfo { JobId = "job-1", Phase = "Encoding", Progress = 42 } };
        var uploads = new Dictionary<string, bool> { ["job-1"] = true };

        var (keep, phase) = Decide(entry, reportedActive: reported, activeUploads: uploads);

        keep.Should().BeTrue();
        phase.Should().BeNull("worker takes precedence over master's upload claim");
    }


    [Fact]
    public void Race_A_three_entries_with_pre_claimed_uploads_all_survive_an_empty_heartbeat()
    {
        // Direct regression guard for Race A: under the fix, the dispatch
        // loop pre-claims _activeUploads synchronously with ActiveJobs.Add.
        // When a heartbeat fires before any of the dispatch tasks have
        // produced a worker-side report, all entries must still survive
        // because their _activeUploads claims hold the slot.
        var entries = new[] { MakeEntry("job-1"), MakeEntry("job-2"), MakeEntry("job-3") };
        var uploads = new Dictionary<string, bool>
        {
            ["job-1"] = true,
            ["job-2"] = true,
            ["job-3"] = true,
        };

        var survivors = entries
            .Select(e => Decide(e, activeUploads: uploads))
            .ToList();

        survivors.Should().AllSatisfy(d =>
        {
            d.Keep.Should().BeTrue();
            d.PhaseOverride.Should().Be("Uploading");
        });
    }


    [Fact]
    public void Race_B_handover_entry_survives_until_worker_report_arrives()
    {
        // Direct regression guard for Race B: the upload's finally has
        // cleared _activeUploads, but the worker has not yet reported the
        // job in its next heartbeat. Without the _remoteJobs criterion,
        // this entry would be dropped and the slot double-booked.
        var entry = MakeEntry("job-1");
        var workItem = new WorkItem { Id = "job-1", AssignedNodeId = NodeId };
        var remote = new Dictionary<string, WorkItem> { ["job-1"] = workItem };

        // Empty worker report, empty uploads, empty downloads, empty completed.
        var (keep, _) = Decide(entry, remoteJobs: remote);

        keep.Should().BeTrue("the master still considers this job assigned to this node");
    }
}
