using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Services.Slots;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins the two permanent fixes for the "queue skips its own head" and
///     "ghost Now-Processing cards" regressions:
///     <list type="bullet">
///         <item><description>A busy device is a CAPACITY deferral — the scheduler must park on
///         the head of the queue instead of rotating the window deeper (which used to walk
///         the whole backlog during one encode and then dispatch low-bitrate items from
///         wherever the window drifted).</description></item>
///         <item><description>Cluster dispatch CLAIMS items instead of dequeue-and-mark-Processing:
///         a claimed item stays Pending and in the queue, invisible to other pickers, so a
///         bounced evaluation never flashes a Processing card and never needs a requeue.</description></item>
///     </list>
/// </summary>
public sealed class SchedulerRotationAndClaimTests : IDisposable
{
    private readonly InMemoryDb _db = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private TranscodingService NewService(out SlotLedger ledger)
    {
        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), _db.CreateRepository());
        ledger = new SlotLedger((_, _) => 1, _ => { });
        svc.SetSlotLedger(ledger);
        return svc;
    }

    /// <summary> Seeds a Queued DB row backed by a real temp file (window sync does File.Exists per row). </summary>
    private async Task<string> SeedRowAsync(MediaFileRepositoryHandle repo, long bitrate, MediaKind kind = MediaKind.Video)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"snacks-sched-{Guid.NewGuid():N}{(kind == MediaKind.Music ? ".flac" : ".mkv")}");
        await File.WriteAllBytesAsync(path, new byte[16]);
        _tempFiles.Add(path);

        await repo.Repo.UpsertAsync(new MediaFile
        {
            FilePath  = path,
            Directory = System.IO.Path.GetDirectoryName(path) ?? "",
            FileName  = System.IO.Path.GetFileName(path),
            BaseName  = System.IO.Path.GetFileNameWithoutExtension(path),
            Bitrate   = bitrate,
            FileSize  = 1_000_000,
            Duration  = 600,
            Status    = MediaFileStatus.Queued,
            Kind      = kind,
            CreatedAt = DateTime.UtcNow,
            LastScannedAt = DateTime.UtcNow,
        });
        return path;
    }

    /// <summary> Tiny wrapper so seeding helpers can share one repo instance per test. </summary>
    private sealed record MediaFileRepositoryHandle(Snacks.Data.MediaFileRepository Repo);

    private MediaFileRepositoryHandle NewRepo() => new(_db.CreateRepository());

    private static HardwareDevice Gpu(string id = "nvidia", params string[] codecs) => new()
    {
        DeviceId        = id,
        SupportedCodecs = codecs.ToList(),
        IsHardware      = true,
    };


    /******************************************************************
     *  RC1 — capacity deferral must not rotate the window
     ******************************************************************/

    [Fact]
    public async Task Busy_device_does_not_rotate_the_window_off_the_head()
    {
        var repo = NewRepo();
        // 60 rows (> QueueWindowSize) in strict bitrate-descending queue order.
        var paths = new List<string>();
        for (int i = 0; i < 60; i++)
            paths.Add(await SeedRowAsync(repo, bitrate: 100_000 - i * 1000));

        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), repo.Repo);
        var ledger = new SlotLedger((_, _) => 1, _ => { });
        svc.SetSlotLedger(ledger);
        svc.SetDetectedDevicesForTest(new List<HardwareDevice> { Gpu("nvidia", "h265") });

        // Occupy the single nvidia slot with a simulated running encode (backed by
        // an active-job entry so the orphan reaper leaves it alone).
        var busy = new WorkItem { FileName = "busy.mkv", Path = "/m/busy.mkv", Kind = MediaKind.Video };
        ledger.TryReserve("master-local", "nvidia", busy.Id, busy.FileName).Should().BeTrue();
        svc.RegisterActiveLocalJobForTest(busy, "nvidia");

        // Run the scheduler long enough for several defer-walk cycles. Pre-fix,
        // each cycle advanced the rotation offset by 50 and evicted the head.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await svc.RunSchedulerForTestAsync(new EncoderOptions { Codec = "h265" }, cts.Token);

        svc.WindowRotationOffsetForTest.Should().Be(0,
            "a full device is a capacity deferral — the window must stay parked on the head");

        var window = svc.GetQueueWindowSnapshot();
        window.Should().HaveCount(50);
        window.Select(w => w.Path).Should().Equal(paths.Take(50),
            "the window must still hold the top-50 rows in queue order");
        window.Should().OnlyContain(w => w.Status == WorkItemStatus.Pending);

        // Slot freed → the very next pick must be the true head of the queue.
        ledger.Release(busy.Id, ReleaseReason.Completed);
        var next = TranscodingService.SelectNextLocalVideoCandidate(
            svc.GetQueueWindowSnapshot(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        next.Should().NotBeNull();
        next!.Path.Should().Be(paths[0], "the highest-bitrate item is the head of the queue");
    }


    [Fact]
    public async Task Unservable_queue_still_rotates_and_exits()
    {
        // Guards the other side of the capacity gate: when NO device can ever serve
        // the items (codec unsupported, no CPU fallback registered), the historical
        // rotate → wrap → break exit must survive, or the scheduler would idle-spin
        // forever at the 200ms poll.
        var repo = NewRepo();
        for (int i = 0; i < 55; i++)
            await SeedRowAsync(repo, bitrate: 100_000 - i * 1000);

        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), repo.Repo);
        svc.SetSlotLedger(new SlotLedger((_, _) => 1, _ => { }));
        // A device that supports no codec at all: eligibility passes nothing, no
        // reserve is ever attempted, so every deferral is a PERMANENT one.
        svc.SetDetectedDevicesForTest(new List<HardwareDevice> { Gpu("nvidia" /* no codecs */) });

        var run = svc.RunSchedulerForTestAsync(new EncoderOptions { Codec = "h265" }, CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        finished.Should().BeSameAs(run, "a fully unservable backlog must wrap and exit, not spin");
        await run;
    }


    /******************************************************************
     *  RC3 — cluster claims: no Processing flash, no requeue on bounce
     ******************************************************************/

    [Fact]
    public async Task Cluster_claim_bounce_never_shows_processing()
    {
        var repo  = NewRepo();
        var pathA = await SeedRowAsync(repo, bitrate: 9000);
        var pathB = await SeedRowAsync(repo, bitrate: 8000);

        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), repo.Repo);
        svc.MarkQueueWindowDirty();
        await svc.SyncQueueWindowAsync();

        var claimed = svc.ClaimForRemoteDispatch();
        claimed.Should().NotBeNull();
        claimed!.Path.Should().Be(pathA, "claims follow queue order");

        // The claim is invisible: still Pending, still in the queue window, and at
        // no point does anything enter the UI's "Now Processing" predicate.
        claimed.Status.Should().Be(WorkItemStatus.Pending);
        svc.GetQueueWindowSnapshot().Should().Contain(w => w.Path == pathA);
        svc.GetAllWorkItems().Should().NotContain(w =>
            w.Status == WorkItemStatus.Processing
            || w.Status == WorkItemStatus.Uploading
            || w.Status == WorkItemStatus.Downloading);

        // A second claim skips the held item.
        var second = svc.ClaimForRemoteDispatch();
        second.Should().NotBeNull();
        second!.Path.Should().Be(pathB);
        svc.ReleaseDispatchClaim(second);

        // Bounce: release makes the item claimable again, with zero state churn.
        svc.ReleaseDispatchClaim(claimed);
        claimed.Status.Should().Be(WorkItemStatus.Pending);
        svc.GetQueueWindowSnapshot().Should().Contain(w => w.Path == pathA);
        svc.ClaimForRemoteDispatch()!.Path.Should().Be(pathA);
    }


    [Fact]
    public async Task Claim_prevents_local_vs_cluster_double_dispatch()
    {
        var repo      = NewRepo();
        var videoPath = await SeedRowAsync(repo, bitrate: 9000);
        var musicPath = await SeedRowAsync(repo, bitrate: 320, kind: MediaKind.Music);

        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), repo.Repo);
        svc.MarkQueueWindowDirty();
        await svc.SyncQueueWindowAsync();

        var video = svc.ClaimForRemoteDispatch(w => w.Kind == MediaKind.Video);
        var music = svc.ClaimForRemoteDispatch(w => w.Kind == MediaKind.Music);
        video!.Path.Should().Be(videoPath);
        music!.Path.Should().Be(musicPath);

        // Local video pick must not see the claimed item.
        TranscodingService.SelectNextLocalVideoCandidate(
                svc.GetQueueWindowSnapshot(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                shouldSkipLocal: null,
                isPathClaimed: svc.IsPathClaimedForDispatch)
            .Should().BeNull("both pending items are claimed by the cluster dispatcher");

        // Nothing left for a third cluster claim either.
        svc.ClaimForRemoteDispatch().Should().BeNull();

        // Commit removes the item from the queue exactly once, with no duplicate
        // Pending entry left behind for the same path.
        svc.CommitDispatchClaim(video);
        svc.GetQueueWindowSnapshot().Should().NotContain(w => w.Path == videoPath);
        svc.IsPathClaimedForDispatch(video.NormalizedPath).Should().BeFalse();

        // The music claim is untouched by the video commit.
        svc.IsPathClaimedForDispatch(music.NormalizedPath).Should().BeTrue();
        svc.ReleaseDispatchClaim(music);
    }


    [Fact]
    public async Task Claimed_item_survives_window_eviction()
    {
        // Window sync evicts Pending items that fell out of the top-N. A claimed
        // item must be exempt — eviction would unregister it and let a later sync
        // rehydrate the same DB row as a duplicate under a fresh id.
        var repo = NewRepo();
        var lowPath = await SeedRowAsync(repo, bitrate: 1); // will fall out of any top-N reshuffle

        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), repo.Repo);
        svc.MarkQueueWindowDirty();
        await svc.SyncQueueWindowAsync();

        var claimed = svc.ClaimForRemoteDispatch();
        claimed!.Path.Should().Be(lowPath);

        // Push the claimed row out of the window's target set entirely (mark the DB
        // row non-queued), then force a resync: the eviction pass would normally
        // remove the in-memory Pending item.
        await repo.Repo.SetStatusAsync(lowPath, MediaFileStatus.Processing);
        svc.MarkQueueWindowDirty();
        await svc.SyncQueueWindowAsync();

        svc.GetQueueWindowSnapshot().Should().Contain(w => w.Path == lowPath,
            "a claimed item must never be evicted mid-evaluation");

        svc.ReleaseDispatchClaim(claimed);
    }
}
