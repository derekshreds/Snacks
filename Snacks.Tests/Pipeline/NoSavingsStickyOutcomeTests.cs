using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Regression coverage for the <c>NoSavings</c> sticky outcome — the second half of the
///     fix for the cluster mux-pass disappearance bug.
///
///     <para>The user's recurring symptom: encode → "no savings" → DB row flipped to <c>Skipped</c>
///     → <c>ReevaluateSkippedAsync</c> flips Skipped → Unseen because <c>WouldSkipUnderOptions</c>
///     reads inflated total-bitrate and disagrees with the empirical outcome → next scan re-queues
///     → encoder runs the same file again with the same result. The fix moves "we tried, no
///     savings" into its own status that <c>ReevaluateSkippedAsync</c> doesn't see, with an
///     opt-in retry path for users who actually changed encoder settings.</para>
/// </summary>
public sealed class NoSavingsStickyOutcomeTests
{
    private static MediaFile MakeMediaFile(string path, MediaFileStatus status = MediaFileStatus.NoSavings) => new()
    {
        FilePath        = path,
        Directory       = "/lib",
        FileName        = System.IO.Path.GetFileName(path),
        BaseName        = System.IO.Path.GetFileNameWithoutExtension(path),
        Codec           = "hevc",
        IsHevc          = true,
        Bitrate         = 14000, // intentionally above target — predicate would flip Skipped → Unseen
        Height          = 1080,
        AudioStreams    = "[{\"l\":\"eng\",\"c\":\"truehd\",\"ch\":8}]",
        SubtitleStreams = "[]",
        Status          = status,
    };

    [Fact]
    public async Task Reevaluate_DoesNotFlipNoSavingsRows_ByDefault()
    {
        // The whole point of NoSavings: ReevaluateSkippedAsync must NOT touch these rows.
        // Otherwise the loop reopens — predicted-skip prediction keeps flipping rows that
        // ffmpeg already proved wouldn't shrink.
        using var db = new InMemoryDb();
        var repo = db.CreateRepository();

        await repo.UpsertAsync(MakeMediaFile("/lib/toy-story-2.mkv", MediaFileStatus.NoSavings));

        // Run the normal Re-evaluate sweep with a predicate that always says "should encode."
        // If the implementation accidentally walked NoSavings rows, the row would flip.
        int flipped = await repo.ReevaluateSkippedAsync(_ => false);

        flipped.Should().Be(0);
        var row = await ReadRow(db, "/lib/toy-story-2.mkv");
        row.Status.Should().Be(MediaFileStatus.NoSavings,
            "Re-evaluate's default Skipped sweep must not touch NoSavings rows — that's the loop closure");
    }

    [Fact]
    public async Task ReevaluateSkippedAsync_StillFlipsLegitimateSkippedRows()
    {
        // Sanity: NoSavings being introduced shouldn't break the existing Skipped → Unseen flow.
        using var db = new InMemoryDb();
        var repo = db.CreateRepository();

        await repo.UpsertAsync(MakeMediaFile("/lib/skipped.mkv", MediaFileStatus.Skipped));

        int flipped = await repo.ReevaluateSkippedAsync(_ => false);

        flipped.Should().Be(1);
        var row = await ReadRow(db, "/lib/skipped.mkv");
        row.Status.Should().Be(MediaFileStatus.Unseen);
    }

    [Fact]
    public async Task ReevaluateNoSavingsAsync_FlipsAllNoSavingsRowsToUnseen()
    {
        // The opt-in retry: when the user ticks "Retry no-savings encodes" on the
        // Re-evaluate panel, this method runs and flips every NoSavings row back to
        // Unseen so the next library scan re-queues them.
        using var db = new InMemoryDb();
        var repo = db.CreateRepository();

        await repo.UpsertAsync(MakeMediaFile("/lib/a.mkv", MediaFileStatus.NoSavings));
        await repo.UpsertAsync(MakeMediaFile("/lib/b.mkv", MediaFileStatus.NoSavings));
        await repo.UpsertAsync(MakeMediaFile("/lib/c.mkv", MediaFileStatus.Completed)); // unrelated, must not flip

        int flipped = await repo.ReevaluateNoSavingsAsync();

        flipped.Should().Be(2);
        (await ReadRow(db, "/lib/a.mkv")).Status.Should().Be(MediaFileStatus.Unseen);
        (await ReadRow(db, "/lib/b.mkv")).Status.Should().Be(MediaFileStatus.Unseen);
        (await ReadRow(db, "/lib/c.mkv")).Status.Should().Be(MediaFileStatus.Completed,
            "Completed rows must be left alone — only NoSavings rows are opt-in retry candidates");
    }

    [Fact]
    public async Task SetStatusAndLastEncodedAtAsync_StampsTimestamp()
    {
        // LastEncodedAt is the empirical anchor — proves "we already tried this" so future
        // diagnostics can tell whether a NoSavings row is fresh or stale.
        using var db = new InMemoryDb();
        var repo = db.CreateRepository();

        await repo.UpsertAsync(MakeMediaFile("/lib/x.mkv", MediaFileStatus.Queued));

        var stamp = new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc);
        await repo.SetStatusAndLastEncodedAtAsync("/lib/x.mkv", MediaFileStatus.NoSavings, stamp);

        var row = await ReadRow(db, "/lib/x.mkv");
        row.Status.Should().Be(MediaFileStatus.NoSavings);
        row.LastEncodedAt.Should().Be(stamp);
    }

    [Fact]
    public void WorkItem_NoSavings_IsTerminalAgainstActiveStateAssignments()
    {
        // The status setter on WorkItem rejects active-state writes once a terminal state is
        // set (so a stale progress callback can't flip a NoSavings tile back to Uploading).
        // Pinned because the queue UI reads WorkItem.Status directly — a regression here
        // would re-enable the "tile says Completed but DB says Skipped" lying we just fixed.
        var wi = new WorkItem { Status = WorkItemStatus.NoSavings };

        wi.Status = WorkItemStatus.Uploading;     // should be rejected
        wi.Status.Should().Be(WorkItemStatus.NoSavings);

        wi.Status = WorkItemStatus.Downloading;   // should be rejected
        wi.Status.Should().Be(WorkItemStatus.NoSavings);

        wi.Status = WorkItemStatus.Processing;    // should be rejected
        wi.Status.Should().Be(WorkItemStatus.NoSavings);

        // Pending is the legitimate retry path and must always be accepted.
        wi.Status = WorkItemStatus.Pending;
        wi.Status.Should().Be(WorkItemStatus.Pending);
    }

    private static async Task<MediaFile> ReadRow(InMemoryDb db, string path)
    {
        await using var ctx = await db.Factory.CreateDbContextAsync();
        return await ctx.MediaFiles.FirstAsync(f => f.FilePath == path);
    }
}
