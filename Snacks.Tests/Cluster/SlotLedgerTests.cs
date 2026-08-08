using FluentAssertions;
using Snacks.Services.Slots;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the contract of the authoritative cluster slot store. Every
///     reservation lifecycle the dispatcher relies on — capacity gating,
///     idempotent release, observation-only heartbeat updates, projection
///     for the dashboard — has a regression guard here. Race A and Race B
///     (which were guarded indirectly by the legacy <c>SlotReconciler</c>
///     preserve-rules) become trivially safe under the ledger because
///     heartbeats can no longer delete reservations.
/// </summary>
public sealed class SlotLedgerTests
{
    private const string NodeA = "node-A";
    private const string NodeB = "node-B";
    private const string Intel = "intel";

    private static SlotLedger MakeLedger(int capacity = 1, Action<string>? log = null) =>
        new((_, _) => capacity, log);

    [Fact]
    public void TryReserve_succeeds_when_under_capacity()
    {
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "j1", "a.mkv").Should().BeTrue();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
        l.GetPhase("j1").Should().Be(SlotPhase.Reserved);
    }

    [Fact]
    public void TryReserve_refuses_when_at_capacity()
    {
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.TryReserve(NodeA, Intel, "j2", null).Should().BeFalse();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void TryReserve_refuses_zero_capacity_devices()
    {
        var l = MakeLedger(capacity: 0); // device disabled or unknown
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeFalse();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(0);
    }

    [Fact]
    public void TryReserve_refuses_duplicate_jobId()
    {
        var l = MakeLedger(capacity: 5);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeFalse();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void Rekey_moves_reservation_to_new_id_preserving_slot_accounting()
    {
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "old-id", "a.mkv").Should().BeTrue();
        l.TransitionPhase("old-id", SlotPhase.Uploading);

        l.Rekey("old-id", "new-id");

        // Release paths key by the NEW id after an upload-resume ID swap —
        // the old id must be gone or the slot leaks until master restart.
        l.Contains("old-id").Should().BeFalse();
        l.Contains("new-id").Should().BeTrue();
        l.GetPhase("new-id").Should().Be(SlotPhase.Uploading);
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);

        l.Release("new-id", ReleaseReason.Completed);
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(0);
    }

    [Fact]
    public void Rekey_is_noop_for_unknown_or_identical_ids()
    {
        var l = MakeLedger(capacity: 2);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();

        l.Rekey("missing", "j2");
        l.Contains("j2").Should().BeFalse();

        l.Rekey("j1", "j1");
        l.Contains("j1").Should().BeTrue();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void Rekey_drops_old_row_when_new_id_already_reserved()
    {
        var l = MakeLedger(capacity: 2);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.TryReserve(NodeA, Intel, "j2", null).Should().BeTrue();

        l.Rekey("j1", "j2");

        l.Contains("j1").Should().BeFalse();
        l.Contains("j2").Should().BeTrue();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void Release_is_idempotent()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", null);
        l.Release("j1", ReleaseReason.Completed);
        l.Release("j1", ReleaseReason.Completed);
        l.Release("nonexistent", ReleaseReason.Cancelled);
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(0);
    }

    [Fact]
    public void TransitionPhase_updates_phase_and_resets_PhaseEnteredAt()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", null);
        var before = l.GetReservation("j1")!.PhaseEnteredAt;
        Thread.Sleep(5);
        l.TransitionPhase("j1", SlotPhase.Uploading);
        l.GetPhase("j1").Should().Be(SlotPhase.Uploading);
        l.GetReservation("j1")!.PhaseEnteredAt.Should().BeAfter(before);
    }

    [Fact]
    public void TransitionPhase_to_same_phase_does_not_reset_timestamp()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", null);
        l.TransitionPhase("j1", SlotPhase.Uploading);
        var entered = l.GetReservation("j1")!.PhaseEnteredAt;
        Thread.Sleep(5);
        l.TransitionPhase("j1", SlotPhase.Uploading); // no-op
        l.GetReservation("j1")!.PhaseEnteredAt.Should().Be(entered);
    }

    [Fact]
    public void UpdateProgress_never_creates_a_reservation()
    {
        var l = MakeLedger();
        l.UpdateProgress("never-reserved", 50, "Encoding");
        l.Contains("never-reserved").Should().BeFalse();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(0);
    }

    [Fact]
    public void UpdateProgress_stamps_progress_and_phase_on_existing_reservation()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", null);
        l.TransitionPhase("j1", SlotPhase.Uploading);
        l.UpdateProgress("j1", 42, "Encoding");
        var r = l.GetReservation("j1")!;
        r.Progress.Should().Be(42);
        r.Phase.Should().Be(SlotPhase.Encoding);
    }

    /// <summary>
    ///     Race A regression: the legacy reconciler dropped optimistic
    ///     reservations when an early heartbeat arrived before the worker
    ///     started reporting. Under the ledger an "empty heartbeat" is
    ///     literally a no-op — there's nothing to drop.
    /// </summary>
    [Fact]
    public void Race_A_three_pre_claimed_reservations_survive_an_empty_heartbeat()
    {
        var l = MakeLedger(capacity: 3);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.TryReserve(NodeA, Intel, "j2", null).Should().BeTrue();
        l.TryReserve(NodeA, Intel, "j3", null).Should().BeTrue();

        // Simulating heartbeat reconcile: caller iterates worker-reported
        // active jobs (none), so UpdateProgress is never called for these
        // ledger entries. They survive untouched.

        l.UsedDeviceSlots(NodeA, Intel).Should().Be(3);
        l.Contains("j1").Should().BeTrue();
        l.Contains("j2").Should().BeTrue();
        l.Contains("j3").Should().BeTrue();
    }

    /// <summary>
    ///     Race B regression: upload completes, master expects worker to
    ///     start reporting it as Encoding next heartbeat. The legacy code
    ///     bridged this gap with <c>_remoteJobs</c> preservation rules; the
    ///     ledger doesn't need them — the reservation simply stays put.
    /// </summary>
    [Fact]
    public void Race_B_handover_phase_transitions_keep_reservation_alive()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", null);
        l.TransitionPhase("j1", SlotPhase.Uploading);
        l.TransitionPhase("j1", SlotPhase.Encoding);

        // Empty heartbeat between transitions — ledger doesn't care.
        l.Contains("j1").Should().BeTrue();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void Cross_node_reservations_are_isolated()
    {
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.TryReserve(NodeB, Intel, "j2", null).Should().BeTrue();
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
        l.UsedDeviceSlots(NodeB, Intel).Should().Be(1);
    }

    [Fact]
    public void Snapshot_returns_wire_compatible_ActiveJobInfo()
    {
        var l = MakeLedger();
        l.TryReserve(NodeA, Intel, "j1", "movie.mkv");
        l.TransitionPhase("j1", SlotPhase.Uploading);
        l.UpdateProgress("j1", 33, null);

        var snap = l.Snapshot(NodeA);
        snap.Should().HaveCount(1);
        snap[0].JobId.Should().Be("j1");
        snap[0].DeviceId.Should().Be(Intel);
        snap[0].FileName.Should().Be("movie.mkv");
        snap[0].Progress.Should().Be(33);
        snap[0].Phase.Should().Be("Uploading");
    }

    [Fact]
    public void Snapshot_omits_reservations_on_other_nodes()
    {
        var l = MakeLedger(capacity: 5);
        l.TryReserve(NodeA, Intel, "j1", null);
        l.TryReserve(NodeB, Intel, "j2", null);
        l.Snapshot(NodeA).Should().ContainSingle(j => j.JobId == "j1");
        l.Snapshot(NodeB).Should().ContainSingle(j => j.JobId == "j2");
    }

    [Fact]
    public void Recovery_rebuild_then_dispatch_respects_capacity()
    {
        // Simulates master restart: ledger is empty, then recovery
        // re-reserves an existing job, then dispatch tries to claim the
        // same slot. Capacity=1 means dispatch must lose.
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "recovered", "in-flight.mkv").Should().BeTrue();
        l.TransitionPhase("recovered", SlotPhase.Encoding);

        l.TryReserve(NodeA, Intel, "fresh-dispatch", "new.mkv").Should().BeFalse();
    }

    [Fact]
    public void Permanently_offline_node_releases_via_NodeFailed_and_frees_slot()
    {
        var l = MakeLedger(capacity: 1);
        l.TryReserve(NodeA, Intel, "j1", null).Should().BeTrue();
        l.Release("j1", ReleaseReason.NodeFailed);

        l.UsedDeviceSlots(NodeA, Intel).Should().Be(0);
        // Slot is reusable — next reservation for the same (node, device) succeeds.
        l.TryReserve(NodeA, Intel, "j2", null).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_TryReserve_against_cap_one_picks_exactly_one_winner()
    {
        var l = MakeLedger(capacity: 1);
        var winners = 0;
        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() =>
            {
                if (l.TryReserve(NodeA, Intel, $"j-{i}", null)) Interlocked.Increment(ref winners);
            }))
            .ToArray();
        await Task.WhenAll(tasks);
        winners.Should().Be(1);
        l.UsedDeviceSlots(NodeA, Intel).Should().Be(1);
    }

    [Fact]
    public void CountByPhase_counts_only_matching_phase()
    {
        var l = MakeLedger(capacity: 10);
        l.TryReserve(NodeA, Intel, "j1", null);
        l.TransitionPhase("j1", SlotPhase.Uploading);
        l.TryReserve(NodeA, Intel, "j2", null);
        l.TransitionPhase("j2", SlotPhase.Encoding);
        l.TryReserve(NodeA, Intel, "j3", null);
        l.TransitionPhase("j3", SlotPhase.Uploading);

        l.CountByPhase(SlotPhase.Uploading).Should().Be(2);
        l.CountByPhase(SlotPhase.Encoding).Should().Be(1);
        l.CountByPhase(SlotPhase.Downloading).Should().Be(0);
    }

    [Fact]
    public void Release_logs_the_reason()
    {
        var entries = new List<string>();
        var l = MakeLedger(capacity: 1, log: entries.Add);
        l.TryReserve(NodeA, Intel, "j1", null);
        l.Release("j1", ReleaseReason.NodeFailed);
        entries.Should().ContainSingle(e => e.Contains("NodeFailed") && e.Contains("j1"));
    }
}
