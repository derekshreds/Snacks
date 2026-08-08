using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins the DB-first pending queue: rows with Status=Queued ARE the queue,
///     ordered by Priority desc then the policy tiebreaker; the scheduler hydrates
///     only the top of that order into its working window; restart resume is a
///     bulk status flip, not a per-row replay.
/// </summary>
public sealed class DbQueueTests : IDisposable
{
    private readonly InMemoryDb _db = new();

    public void Dispose() => _db.Dispose();

    private static MediaFile Row(string path, long bitrate, int priority = 0,
        MediaFileStatus status = MediaFileStatus.Queued, string? nodeId = null,
        DateTime? createdAt = null) => new()
    {
        FilePath  = path,
        Directory = System.IO.Path.GetDirectoryName(path) ?? "",
        FileName  = System.IO.Path.GetFileName(path),
        BaseName  = System.IO.Path.GetFileNameWithoutExtension(path),
        Bitrate   = bitrate,
        FileSize  = 1_000_000,
        Duration  = 600,
        Status    = status,
        Priority  = priority,
        AssignedNodeId = nodeId,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        LastScannedAt = DateTime.UtcNow,
    };

    /******************************************************************
     *  Ordering
     ******************************************************************/

    [Fact]
    public async Task Window_orders_by_priority_then_bitrate()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/low.mkv",  1000));
        await repo.UpsertAsync(Row("/m/high.mkv", 9000));
        await repo.UpsertAsync(Row("/m/front.mkv", 500, priority: 5));

        var window = await repo.GetQueueWindowAsync(10);
        window.Select(r => r.FileName).Should().Equal("front.mkv", "high.mkv", "low.mkv");
    }

    [Fact]
    public async Task Window_excludes_remote_assigned_and_non_queued_rows()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/local.mkv",   1000));
        await repo.UpsertAsync(Row("/m/remote.mkv",  9000, nodeId: "node-1"));
        await repo.UpsertAsync(Row("/m/done.mkv",    9000, status: MediaFileStatus.Completed));

        var window = await repo.GetQueueWindowAsync(10);
        window.Should().ContainSingle(r => r.FileName == "local.mkv");
    }

    [Fact]
    public async Task Newest_first_policy_orders_by_recency_within_priority()
    {
        var repo = _db.CreateRepository();
        var old  = DateTime.UtcNow.AddDays(-30);
        await repo.UpsertAsync(Row("/m/old-big.mkv",  9000, createdAt: old));
        await repo.UpsertAsync(Row("/m/new-small.mkv", 800, createdAt: DateTime.UtcNow));

        var window = await repo.GetQueueWindowAsync(10, newestFirst: true);
        window[0].FileName.Should().Be("new-small.mkv");
    }

    /******************************************************************
     *  Paging + priority
     ******************************************************************/

    [Fact]
    public async Task Queued_page_returns_slice_and_total()
    {
        var repo = _db.CreateRepository();
        for (int i = 0; i < 7; i++)
            await repo.UpsertAsync(Row($"/m/f{i}.mkv", bitrate: 1000 + i));

        var (rows, total) = await repo.GetQueuedPageAsync(skip: 2, take: 3);
        total.Should().Be(7);
        rows.Should().HaveCount(3);
        rows[0].Bitrate.Should().Be(1004); // bitrate desc: 1006,1005 skipped → 1004
    }

    [Fact]
    public async Task Bump_priority_moves_row_to_front_and_refuses_non_queued()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/a.mkv", 9000, priority: 3));
        await repo.UpsertAsync(Row("/m/b.mkv", 1000));
        await repo.UpsertAsync(Row("/m/done.mkv", 500, status: MediaFileStatus.Completed));

        var b    = (await repo.GetByPathAsync("/m/b.mkv"))!;
        var done = (await repo.GetByPathAsync("/m/done.mkv"))!;

        (await repo.BumpPriorityToFrontAsync(b.Id)).Should().Be(4);
        (await repo.BumpPriorityToFrontAsync(done.Id)).Should().BeNull();

        var window = await repo.GetQueueWindowAsync(10);
        window[0].FileName.Should().Be("b.mkv");
    }

    [Fact]
    public async Task Queued_row_status_flip_is_guarded_to_queued_rows()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/q.mkv", 1000));
        await repo.UpsertAsync(Row("/m/p.mkv", 1000, status: MediaFileStatus.Processing));

        var q = (await repo.GetByPathAsync("/m/q.mkv"))!;
        var p = (await repo.GetByPathAsync("/m/p.mkv"))!;

        (await repo.SetQueuedRowStatusAsync(q.Id, MediaFileStatus.Cancelled)).Should().BeTrue();
        (await repo.SetQueuedRowStatusAsync(p.Id, MediaFileStatus.Cancelled)).Should().BeFalse();

        (await repo.GetByPathAsync("/m/q.mkv"))!.Status.Should().Be(MediaFileStatus.Cancelled);
        (await repo.GetByPathAsync("/m/p.mkv"))!.Status.Should().Be(MediaFileStatus.Processing);
    }

    /******************************************************************
     *  Restart resume + bulk operations
     ******************************************************************/

    [Fact]
    public async Task Restart_resume_requeues_only_orphaned_local_processing_rows()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/crashed.mkv", 1000, status: MediaFileStatus.Processing));
        await repo.UpsertAsync(Row("/m/remote.mkv",  1000, status: MediaFileStatus.Processing, nodeId: "node-1"));
        await repo.UpsertAsync(Row("/m/queued.mkv",  1000));

        (await repo.RequeueOrphanedLocalProcessingAsync()).Should().Be(1);

        (await repo.GetByPathAsync("/m/crashed.mkv"))!.Status.Should().Be(MediaFileStatus.Queued);
        (await repo.GetByPathAsync("/m/remote.mkv"))!.Status.Should().Be(MediaFileStatus.Processing);
        (await repo.CountQueuedLocalAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Reset_all_queued_flips_local_backlog_to_unseen_and_clears_priority()
    {
        var repo = _db.CreateRepository();
        await repo.UpsertAsync(Row("/m/a.mkv", 1000, priority: 7));
        await repo.UpsertAsync(Row("/m/remote.mkv", 1000, nodeId: "node-1"));

        (await repo.ResetAllQueuedAsync()).Should().Be(1);

        var a = (await repo.GetByPathAsync("/m/a.mkv"))!;
        a.Status.Should().Be(MediaFileStatus.Unseen);
        a.Priority.Should().Be(0);
        (await repo.GetByPathAsync("/m/remote.mkv"))!.Status.Should().Be(MediaFileStatus.Queued);
    }

    [Fact]
    public async Task Reevaluate_queued_flips_predicate_matches_and_spares_summaryless_rows()
    {
        var repo = _db.CreateRepository();
        var withSummary = Row("/m/skippable.mkv", 1000);
        withSummary.AudioStreams = "[]";
        await repo.UpsertAsync(withSummary);
        await repo.UpsertAsync(Row("/m/no-summary.mkv", 1000)); // AudioStreams null → spared

        int flipped = await repo.ReevaluateQueuedAsync(_ => true);

        flipped.Should().Be(1);
        (await repo.GetByPathAsync("/m/skippable.mkv"))!.Status.Should().Be(MediaFileStatus.Skipped);
        (await repo.GetByPathAsync("/m/no-summary.mkv"))!.Status.Should().Be(MediaFileStatus.Queued);
    }

    /******************************************************************
     *  Window hydration (service level)
     ******************************************************************/

    [Fact]
    public async Task Window_sync_hydrates_top_rows_and_quarantines_missing_files()
    {
        var repo = _db.CreateRepository();

        // A real file on disk (hydratable) + a path that doesn't exist (quarantined).
        var realFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"snacks-test-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(realFile, new byte[16]);
        try
        {
            await repo.UpsertAsync(Row(realFile, 5000));
            await repo.UpsertAsync(Row("/definitely/missing/file.mkv", 9000));

            var service = new TranscodingService(
                new FileService(), new FfprobeService(), new NullHubContext(), repo);

            service.MarkQueueWindowDirty();
            await service.SyncQueueWindowAsync();

            var window = service.GetQueueWindowSnapshot();
            window.Should().ContainSingle(w => w.Path == realFile)
                  .Which.Status.Should().Be(WorkItemStatus.Pending);
            window.Should().NotContain(w => w.Path == "/definitely/missing/file.mkv");

            // The missing row was quarantined back to Unseen, not left clogging the queue.
            (await repo.GetByPathAsync("/definitely/missing/file.mkv"))!
                .Status.Should().Be(MediaFileStatus.Unseen);

            // Idempotent: a second sync must not duplicate the hydrated item.
            service.MarkQueueWindowDirty();
            await service.SyncQueueWindowAsync();
            service.GetQueueWindowSnapshot().Should().HaveCount(1);
        }
        finally
        {
            File.Delete(realFile);
        }
    }

    [Fact]
    public async Task Kind_filtered_window_returns_only_that_kind()
    {
        var repo = _db.CreateRepository();
        for (int i = 0; i < 5; i++)
            await repo.UpsertAsync(Row($"/m/v{i}.mkv", 9000 + i));
        var song = Row("/m/song.flac", 900);
        song.Kind = MediaKind.Music;
        await repo.UpsertAsync(song);

        // The sync uses this to guarantee music representation when high-bitrate
        // video would otherwise fill every window slot.
        var music = await repo.GetQueueWindowAsync(8, kind: MediaKind.Music);
        music.Should().ContainSingle().Which.FileName.Should().Be("song.flac");
    }

    [Fact]
    public void Newest_first_comparator_orders_by_queue_recency()
    {
        var older = new WorkItem { Bitrate = 9000, QueuedAt = DateTime.UtcNow.AddDays(-2) };
        var newer = new WorkItem { Bitrate = 500,  QueuedAt = DateTime.UtcNow };
        var front = new WorkItem { Bitrate = 100,  QueuedAt = DateTime.UtcNow.AddDays(-9), Priority = 1 };

        var items = new List<WorkItem> { older, newer, front };
        items.Sort((a, b) => TranscodingService.CompareQueueOrder(a, b, newestFirst: true));
        items.Should().Equal(front, newer, older);
    }

    [Fact]
    public void Row_id_addressing_parses_only_mf_prefixed_ids()
    {
        TranscodingService.TryParseRowId("mf-123", out var id).Should().BeTrue();
        id.Should().Be(123);
        TranscodingService.TryParseRowId(Guid.NewGuid().ToString(), out _).Should().BeFalse();
        TranscodingService.TryParseRowId("mf-abc", out _).Should().BeFalse();
    }

    /******************************************************************
     *  Null SignalR plumbing (the service broadcasts on hydrate/cancel)
     ******************************************************************/

    private sealed class NullHubContext : IHubContext<TranscodingHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups { get; } = new NullGroups();

        private sealed class NullClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new NullProxy();
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class NullProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class NullGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }
}
