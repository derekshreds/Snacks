using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Regression suite for the "user added an audio output, then removed it" round-trip.
///     The bug: settings-save was one-directional — Skipped → Unseen for newly-eligible
///     files, but never the reverse. After a setting was reverted, items already flipped
///     to Unseen (or already queued as Pending) kept running.
///
///     This file pins both directions:
///     1. <see cref="MediaFileRepository.ReevaluateSkippedAsync"/> still flips Skipped → Unseen
///        when settings make a file newly eligible.
///     2. <see cref="MediaFileRepository.ReevaluateUnseenAsync"/> flips Unseen → Skipped when
///        settings revert and the file no longer needs encoding.
///     3. <see cref="TranscodingService.WouldSkipUnderOptions"/> is the predicate driving both,
///        and round-trips correctly across an audio-output add/remove cycle.
/// </summary>
public sealed class SettingsRoundTripTests
{
    /// <summary>
    ///     Round-trip the underlying predicate across "add audio output → remove audio output".
    ///     Reproduces the user's bug: in Hybrid (or MuxOnly) mode, adding an audio output
    ///     flips at-target files out of Skipped because <c>HasMuxableWork</c> now returns
    ///     true. Removing the output must flip the predicate back so the inverse-direction
    ///     re-evaluation can restore Skipped status. (Pure Transcode mode wouldn't surface
    ///     the bug — the bitrate ceiling alone keeps at-target HEVC skipped regardless of
    ///     audio outputs.)
    /// </summary>
    [Fact]
    public void WouldSkipUnderOptions_round_trips_across_audio_output_toggle()
    {
        var mf = new MediaFile
        {
            FilePath        = "/lib/movie.mkv",
            Directory       = "/lib",
            FileName        = "movie.mkv",
            BaseName        = "movie",
            Codec           = "hevc",
            IsHevc          = true,
            Is4K            = false,
            Bitrate         = 3000,
            Height          = 1080,
            AudioStreams    = "[{\"l\":\"eng\",\"c\":\"aac\",\"ch\":2}]",
            SubtitleStreams = "[]",
        };

        var baseOpts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Hybrid,
            MuxStreams              = MuxStreams.Both,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new() { "en" },
            SubtitleLanguagesToKeep = new() { "en" },
        };

        // Initial: at-target, no muxable work → skip-eligible.
        TranscodingService.WouldSkipUnderOptions(mf, baseOpts).Should().BeTrue();

        // Add Opus 5.1 output → AAC stereo source doesn't dedup against it →
        // HasMuxableWork fires → no longer skip-eligible.
        var withOutput = baseOpts.Clone();
        withOutput.AudioOutputs.Add(new AudioOutputProfile { Codec = "opus", Layout = "5.1", BitrateKbps = 256 });
        TranscodingService.WouldSkipUnderOptions(mf, withOutput).Should().BeFalse();

        // Revert → predicate flips back.
        var reverted = baseOpts.Clone();
        TranscodingService.WouldSkipUnderOptions(mf, reverted).Should().BeTrue();
    }


    // =====================================================================
    //  Direction A: ReevaluateSkippedAsync flips Skipped → Unseen when the
    //  settings change makes a file newly eligible.
    // =====================================================================

    [Fact]
    public async Task ReevaluateSkippedAsync_flips_skipped_rows_to_unseen_when_predicate_says_no_longer_skipped()
    {
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        await SeedAsync(db, new MediaFile
        {
            FilePath     = "/lib/a.mkv", Directory = "/lib", FileName = "a.mkv", BaseName = "a",
            Status       = MediaFileStatus.Skipped,
            AudioStreams = "[{\"l\":\"eng\",\"c\":\"aac\",\"ch\":2}]",
        });

        // Predicate says "no, this should NOT stay skipped."
        var flipped = await repo.ReevaluateSkippedAsync(_ => false);

        flipped.Should().Be(1);
        var row = await GetByPathAsync(db, "/lib/a.mkv");
        row!.Status.Should().Be(MediaFileStatus.Unseen);
        row.LastScannedAt.Should().BeNull();
    }


    [Fact]
    public async Task ReevaluateSkippedAsync_leaves_rows_alone_when_predicate_keeps_them_skipped()
    {
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        await SeedAsync(db, new MediaFile
        {
            FilePath = "/lib/b.mkv", Directory = "/lib", FileName = "b.mkv", BaseName = "b",
            Status   = MediaFileStatus.Skipped,
            AudioStreams = "[]",
        });

        var flipped = await repo.ReevaluateSkippedAsync(_ => true);

        flipped.Should().Be(0);
        var row = await GetByPathAsync(db, "/lib/b.mkv");
        row!.Status.Should().Be(MediaFileStatus.Skipped);
    }


    // =====================================================================
    //  Direction B: ReevaluateUnseenAsync flips Unseen → Skipped when the
    //  settings change makes a previously-eligible file no longer need encoding.
    // =====================================================================

    [Fact]
    public async Task ReevaluateUnseenAsync_flips_unseen_rows_to_skipped_when_predicate_says_should_skip()
    {
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        // Two unseen rows with stream summaries — the kind that got flipped here when
        // the user accidentally added then removed an audio output.
        await SeedAsync(db,
            new MediaFile
            {
                FilePath  = "/lib/c.mkv", Directory = "/lib", FileName = "c.mkv", BaseName = "c",
                Status    = MediaFileStatus.Unseen,
                AudioStreams = "[{\"l\":\"eng\",\"c\":\"aac\",\"ch\":2}]",
            },
            new MediaFile
            {
                FilePath  = "/lib/d.mkv", Directory = "/lib", FileName = "d.mkv", BaseName = "d",
                Status    = MediaFileStatus.Unseen,
                AudioStreams = "[]",
            });

        var flipped = await repo.ReevaluateUnseenAsync(_ => true);

        flipped.Should().Be(2);
        (await GetByPathAsync(db, "/lib/c.mkv"))!.Status.Should().Be(MediaFileStatus.Skipped);
        (await GetByPathAsync(db, "/lib/d.mkv"))!.Status.Should().Be(MediaFileStatus.Skipped);
    }


    [Fact]
    public async Task ReevaluateUnseenAsync_ignores_rows_without_stream_summaries()
    {
        // Legacy rows scanned before stream-summary persistence existed: AudioStreams
        // and SubtitleStreams both null. Re-evaluating them would require a re-probe;
        // safer to leave them Unseen and let the next scan handle them.
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        await SeedAsync(db, new MediaFile
        {
            FilePath = "/lib/legacy.mkv", Directory = "/lib", FileName = "legacy.mkv", BaseName = "legacy",
            Status   = MediaFileStatus.Unseen,
            AudioStreams    = null,
            SubtitleStreams = null,
        });

        var flipped = await repo.ReevaluateUnseenAsync(_ => true);

        flipped.Should().Be(0);
        var row = await GetByPathAsync(db, "/lib/legacy.mkv");
        row!.Status.Should().Be(MediaFileStatus.Unseen);
    }


    [Fact]
    public async Task ReevaluateUnseenAsync_leaves_rows_alone_when_predicate_says_not_skip()
    {
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        await SeedAsync(db, new MediaFile
        {
            FilePath = "/lib/e.mkv", Directory = "/lib", FileName = "e.mkv", BaseName = "e",
            Status   = MediaFileStatus.Unseen,
            AudioStreams = "[]",
        });

        var flipped = await repo.ReevaluateUnseenAsync(_ => false);

        flipped.Should().Be(0);
        (await GetByPathAsync(db, "/lib/e.mkv"))!.Status.Should().Be(MediaFileStatus.Unseen);
    }


    [Fact]
    public async Task ReevaluateUnseenAsync_does_not_touch_other_statuses()
    {
        // Only Unseen rows are eligible. Completed and Failed rows are terminal-ish — we
        // must not silently flip them to Skipped because settings changed.
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        await SeedAsync(db,
            new MediaFile { FilePath = "/lib/c.mkv", Directory = "/lib", FileName = "c.mkv", BaseName = "c",
                            Status = MediaFileStatus.Completed,  AudioStreams = "[]" },
            new MediaFile { FilePath = "/lib/f.mkv", Directory = "/lib", FileName = "f.mkv", BaseName = "f",
                            Status = MediaFileStatus.Failed,     AudioStreams = "[]" });

        var flipped = await repo.ReevaluateUnseenAsync(_ => true);

        flipped.Should().Be(0);
        (await GetByPathAsync(db, "/lib/c.mkv"))!.Status.Should().Be(MediaFileStatus.Completed);
        (await GetByPathAsync(db, "/lib/f.mkv"))!.Status.Should().Be(MediaFileStatus.Failed);
    }


    // =====================================================================
    //  End-to-end via the live predicate: simulates the user's bug report.
    //  Add an output → previously-Skipped file flips Unseen via direction A.
    //  Remove the output → that same Unseen file flips back via direction B.
    // =====================================================================

    [Fact]
    public async Task Round_trip_through_repo_using_real_predicate()
    {
        using var db   = new InMemoryDb();
        var       repo = db.CreateRepository();

        var mf = new MediaFile
        {
            FilePath     = "/lib/round.mkv", Directory = "/lib", FileName = "round.mkv", BaseName = "round",
            Codec        = "hevc",
            IsHevc       = true,
            Is4K         = false,
            Bitrate      = 3000,
            Height       = 1080,
            Status       = MediaFileStatus.Skipped,
            AudioStreams = "[{\"l\":\"eng\",\"c\":\"aac\",\"ch\":2}]",
        };
        await SeedAsync(db, mf);

        var baseOpts = new EncoderOptions
        {
            Encoder                = "libx265",
            TargetBitrate          = 3500,
            SkipPercentAboveTarget = 20,
            EncodingMode           = EncodingMode.Hybrid,
            MuxStreams             = MuxStreams.Both,
            PreserveOriginalAudio  = true,
            AudioOutputs           = new(),
            AudioLanguagesToKeep   = new() { "en" },
        };
        var withOutput = baseOpts.Clone();
        withOutput.AudioOutputs.Add(new AudioOutputProfile { Codec = "opus", Layout = "5.1", BitrateKbps = 256 });

        // 1. User adds the output. Direction A re-evaluates Skipped rows.
        // ReevaluateSkippedAsync flips a row to Unseen when the predicate returns false.
        // Under withOutput: WouldSkipUnderOptions=false → flip.
        var requeued = await repo.ReevaluateSkippedAsync(m =>
            TranscodingService.WouldSkipUnderOptions(m, withOutput));
        requeued.Should().Be(1);
        (await GetByPathAsync(db, "/lib/round.mkv"))!.Status.Should().Be(MediaFileStatus.Unseen);

        // 2. User reverts the output. Direction B re-evaluates Unseen rows.
        var reskipped = await repo.ReevaluateUnseenAsync(m =>
            TranscodingService.WouldSkipUnderOptions(m, baseOpts));
        reskipped.Should().Be(1);
        (await GetByPathAsync(db, "/lib/round.mkv"))!.Status.Should().Be(MediaFileStatus.Skipped);
    }


    // =====================================================================
    //  Helpers
    // =====================================================================

    private static async Task SeedAsync(InMemoryDb db, params MediaFile[] rows)
    {
        using var ctx = db.Factory.CreateDbContext();
        ctx.MediaFiles.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private static async Task<MediaFile?> GetByPathAsync(InMemoryDb db, string path)
    {
        using var ctx = db.Factory.CreateDbContext();
        return await ctx.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == path);
    }
}
