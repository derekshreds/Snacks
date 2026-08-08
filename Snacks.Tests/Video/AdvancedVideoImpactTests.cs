using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

public sealed class AdvancedVideoImpactTests
{
    [Fact]
    public void Tiered_policy_buckets_by_profile_action_and_rule()
    {
        var fourK = Profile("AV1 4K", 32);
        var hd = Profile("AV1 1080p", 35);
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [fourK, hd],
                Rules =
                [
                    Rule("4K sources", fourK.Id,
                        Condition(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"),
                        Condition(VideoRuleField.ResolutionClass, VideoRuleOperator.Is, "2160p+")),
                    Rule("Everything else", hd.Id,
                        Condition(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1")),
                    new VideoRule
                    {
                        Name = "Already AV1",
                        Action = AdvancedVideoAction.Skip,
                        Conditions = [Condition(VideoRuleField.Codec, VideoRuleOperator.Is, "av1")],
                    },
                ],
            },
        };

        var rows = new (MediaFile, EncoderOptionsOverride?)[]
        {
            (Video("a.mkv", "h264", 3840, 2160), null),
            (Video("b.mkv", "hevc", 3840, 2160), null),
            (Video("c.mkv", "h264", 1920, 1080), null),
            (Video("d.mkv", "h264", 1280, 720), null),
            (Video("e.mkv", "av1", 1920, 1080), null),
        };

        var result = AdvancedVideoImpactService.Aggregate(candidate, rows);

        result.Analyzed.Should().Be(5);
        result.Buckets.Should().HaveCount(3);

        var top = result.Buckets[0];
        top.ProfileName.Should().Be("AV1 1080p");
        top.Count.Should().Be(2);
        top.RuleNames.Should().Equal("Everything else");

        result.Buckets.Single(b => b.ProfileName == "AV1 4K").Count.Should().Be(2);
        result.Buckets.Single(b => b.Action == AdvancedVideoAction.Skip).Count.Should().Be(1);

        var rules = candidate.AdvancedVideo.Rules;
        result.RuleCounts[rules[0].Id].Should().Be(2);
        result.RuleCounts[rules[1].Id].Should().Be(2);
        result.RuleCounts[rules[2].Id].Should().Be(1);
        result.UnmatchedCount.Should().Be(0);
    }

    [Fact]
    public void Files_no_rule_matches_count_toward_the_default_action()
    {
        var av1 = Profile("AV1", 32);
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [av1],
                Rules = [Rule("4K only", av1.Id, Condition(VideoRuleField.ResolutionClass, VideoRuleOperator.Is, "2160p+"))],
            },
        };

        var result = AdvancedVideoImpactService.Aggregate(candidate,
        [
            (Video("a.mkv", "h264", 3840, 2160), null),
            (Video("b.mkv", "h264", 1920, 1080), null),
            (Video("c.mkv", "h264", 1280, 720), null),
        ]);

        result.UnmatchedCount.Should().Be(2);
        result.Buckets.Single(b => b.Action == AdvancedVideoAction.UseSimpleSettings).Count.Should().Be(2);
    }

    [Fact]
    public void Folder_override_forcing_simple_is_reflected_per_file()
    {
        var av1 = Profile("AV1", 32);
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [av1],
                Rules = [Rule("all", av1.Id, Condition(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))],
            },
        };
        var simpleFolder = new EncoderOptionsOverride { AdvancedVideoPolicy = AdvancedVideoFolderPolicy.Simple };

        var result = AdvancedVideoImpactService.Aggregate(candidate,
        [
            (Video("a.mkv", "h264", 1920, 1080), null),
            (Video("b.mkv", "h264", 1920, 1080), simpleFolder),
        ]);

        result.Buckets.Should().HaveCount(2);
        result.Buckets.Single(b => b.Action == AdvancedVideoAction.TranscodeWithProfile).Count.Should().Be(1);
        result.Buckets.Single(b => b.Action == AdvancedVideoAction.UseSimpleSettings).Count.Should().Be(1);
    }

    [Fact]
    public void Blocked_plans_get_their_own_bucket_with_the_reason()
    {
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions { Enabled = true, Profiles = [null!] },
        };

        var result = AdvancedVideoImpactService.Aggregate(candidate,
            [(Video("a.mkv", "h264", 1920, 1080), null)]);

        var bucket = result.Buckets.Single();
        bucket.Blocked.Should().BeTrue();
        bucket.BlockingReason.Should().Contain("Profile entries must be objects");
    }

    [Fact]
    public void Samples_are_capped_but_counts_keep_growing()
    {
        var av1 = Profile("AV1", 32);
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [av1],
                Rules = [Rule("all", av1.Id, Condition(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))],
            },
        };
        var rows = Enumerable.Range(0, 20)
            .Select(i => ((MediaFile, EncoderOptionsOverride?))(Video($"file{i}.mkv", "h264", 1920, 1080), null));

        var bucket = AdvancedVideoImpactService.Aggregate(candidate, rows, sampleLimit: 5).Buckets.Single();

        bucket.Count.Should().Be(20);
        bucket.Samples.Should().HaveCount(5);
    }

    [Fact]
    public void Quality_buckets_report_current_bytes_but_never_forecast()
    {
        var av1 = Profile("AV1", 32);
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [av1],
                Rules = [Rule("all", av1.Id, Condition(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))],
            },
        };

        var bucket = AdvancedVideoImpactService.Aggregate(candidate,
        [
            (Video("a.mkv", "h264", 1920, 1080), null),
            (Video("b.mkv", "h264", 1920, 1080), null),
        ]).Buckets.Single();

        bucket.TotalBytes.Should().Be(8_000_000_000);
        bucket.ProjectedBytes.Should().BeNull("quality-mode output size depends on content");
    }

    [Fact]
    public void Bitrate_buckets_forecast_target_times_duration()
    {
        var bitrate = new VideoEncodingProfile
        {
            Name = "HEVC 3500",
            Codec = "h265",
            Preset = null,
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Bitrate, TargetKbps = 3500 },
        };
        var candidate = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [bitrate],
                Rules = [Rule("all", bitrate.Id, Condition(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))],
            },
        };

        var bucket = AdvancedVideoImpactService.Aggregate(candidate,
            [(Video("a.mkv", "h264", 1920, 1080), null)]).Buckets.Single();

        // 3500 kb/s × 125 bytes-per-kbit-second × 5400 s (the synthetic duration).
        bucket.ProjectedBytes.Should().Be((long)(3500 * 125.0 * 5400));
    }

    [Fact]
    public void Empty_library_produces_no_buckets()
    {
        var result = AdvancedVideoImpactService.Aggregate(new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions { Enabled = true },
        }, []);

        result.Analyzed.Should().Be(0);
        result.Buckets.Should().BeEmpty();
    }

    private static VideoEncodingProfile Profile(string name, double quality) => new()
    {
        Name = name,
        Codec = "av1",
        Preset = null,
        RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = quality },
        OutputRetention = VideoOutputRetention.AlwaysKeep,
    };

    private static VideoRule Rule(string name, Guid profileId, params VideoRuleCondition[] conditions) => new()
    {
        Name = name,
        Action = AdvancedVideoAction.TranscodeWithProfile,
        ProfileId = profileId,
        Conditions = [.. conditions],
    };

    private static VideoRuleCondition Condition(VideoRuleField field, VideoRuleOperator op, string value) =>
        new() { Field = field, Operator = op, Values = [value] };

    private static MediaFile Video(string name, string codec, int width, int height) => new()
    {
        FileName = name,
        FilePath = $"/library/{name}",
        Codec = codec,
        Width = width,
        Height = height,
        Bitrate = 8000,
        FileSize = 4_000_000_000,
        Duration = 5400,
        Kind = MediaKind.Video,
        IsHdr = false,
        Is4K = width > 1920,
    };
}
