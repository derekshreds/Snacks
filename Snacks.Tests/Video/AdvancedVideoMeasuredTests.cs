using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

public sealed class AdvancedVideoMeasuredTests
{
    [Fact]
    public void Groups_by_profile_and_computes_weighted_output_bitrate()
    {
        var rows = new List<EncodeHistory>
        {
            Row("AV1 4K", originalBytes: 50_000_000_000, encodedBytes: 10_000_000_000, saved: 40_000_000_000, seconds: 8000),
            Row("AV1 4K", originalBytes: 40_000_000_000, encodedBytes: 8_000_000_000, saved: 32_000_000_000, seconds: 8000),
            Row("AV1 1080p", originalBytes: 8_000_000_000, encodedBytes: 2_000_000_000, saved: 6_000_000_000, seconds: 6000),
        };

        var measures = AdvancedVideoMeasuredService.Aggregate(rows);

        measures.Should().HaveCount(2);
        var fourK = measures[0];
        fourK.ProfileName.Should().Be("AV1 4K");
        fourK.Jobs.Should().Be(2);
        fourK.BytesSaved.Should().Be(72_000_000_000);
        // 18e9 bytes × 8 / 1000 / 16000 s = 9000 kb/s.
        fourK.AvgEncodedKbps.Should().Be(9000);
    }

    [Fact]
    public void Discarded_no_savings_rows_count_as_jobs_but_not_bitrate()
    {
        var rows = new List<EncodeHistory>
        {
            Row("HEVC", originalBytes: 4_000_000_000, encodedBytes: 1_000_000_000, saved: 3_000_000_000, seconds: 5000),
            Row("HEVC", originalBytes: 3_000_000_000, encodedBytes: 0, saved: 0, seconds: 5000),
        };

        var measure = AdvancedVideoMeasuredService.Aggregate(rows).Single();

        measure.Jobs.Should().Be(2);
        measure.Kept.Should().Be(1);
        measure.Discarded.Should().Be(1);
        measure.OriginalBytes.Should().Be(4_000_000_000, "discarded outputs contribute no size comparison");
        measure.AvgEncodedKbps.Should().Be((long)(1_000_000_000 * 8.0 / 1000.0 / 5000));
    }

    [Fact]
    public void Simple_jobs_without_labels_are_excluded_entirely()
    {
        var rows = new List<EncodeHistory>
        {
            new() { OriginalSizeBytes = 1, EncodedSizeBytes = 1, DurationSeconds = 1 },
        };

        AdvancedVideoMeasuredService.Aggregate(rows).Should().BeEmpty();
    }

    [Fact]
    public void Latest_profile_id_wins_when_a_name_was_reused()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var rows = new List<EncodeHistory>
        {
            Row("AV1", 1, 1, 0, 1, oldId, new DateTime(2026, 1, 1)),
            Row("AV1", 1, 1, 0, 1, newId, new DateTime(2026, 6, 1)),
        };

        AdvancedVideoMeasuredService.Aggregate(rows).Single().ProfileId.Should().Be(newId);
    }

    private static EncodeHistory Row(
        string profile, long originalBytes, long encodedBytes, long saved, double seconds,
        Guid? profileId = null, DateTime? completed = null) => new()
    {
        AdvancedProfileName = profile,
        AdvancedProfileId   = profileId ?? Guid.NewGuid(),
        OriginalSizeBytes   = originalBytes,
        EncodedSizeBytes    = encodedBytes,
        BytesSaved          = saved,
        DurationSeconds     = seconds,
        CompletedAt         = completed ?? DateTime.UtcNow,
    };
}
