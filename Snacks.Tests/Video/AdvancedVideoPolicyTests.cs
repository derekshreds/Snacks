using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

public sealed class AdvancedVideoPolicyTests
{
    [Fact]
    public void Disabled_advanced_layer_is_an_exact_simple_override_merge()
    {
        var options = new EncoderOptions { TargetBitrate = 3500 };
        var folder = new EncoderOptionsOverride { TargetBitrate = 5000 };

        var resolved = VideoPolicyResolver.Resolve(options, folder, null, Facts("h264"));

        resolved.Plan.Action.Should().Be(AdvancedVideoAction.UseSimpleSettings);
        resolved.Plan.IsAdvanced.Should().BeFalse();
        resolved.Options.TargetBitrate.Should().Be(5000);
        options.TargetBitrate.Should().Be(3500);
    }

    [Fact]
    public void First_matching_codec_not_av1_rule_forces_exact_libaom_profile()
    {
        var profile = LibaomProfile();
        var options = Enabled(profile,
            Rule("not av1", VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1", profile));

        var resolved = VideoPolicyResolver.Resolve(options, null, null, Facts("hevc"));

        resolved.Plan.Action.Should().Be(AdvancedVideoAction.TranscodeWithProfile);
        resolved.Plan.RuleName.Should().Be("not av1");
        resolved.Plan.ProfileName.Should().Be(profile.Name);
        resolved.Plan.ExplicitEncoder.Should().Be("libaom-av1");
        resolved.Options.Codec.Should().Be("av1");
        resolved.Options.Encoder.Should().Be("libaom-av1");
    }

    [Fact]
    public void All_rule_combines_codec_resolution_and_bitrate()
    {
        var profile = LibaomProfile();
        var rule = new VideoRule
        {
            Name = "large h264",
            Match = VideoRuleMatchMode.All,
            Action = AdvancedVideoAction.TranscodeWithProfile,
            ProfileId = profile.Id,
            Conditions =
            [
                new() { Field = VideoRuleField.Codec, Operator = VideoRuleOperator.Is, Values = ["h264"] },
                new() { Field = VideoRuleField.Height, Operator = VideoRuleOperator.GreaterThanOrEqual, Values = ["1080"] },
                new() { Field = VideoRuleField.BitrateKbps, Operator = VideoRuleOperator.GreaterThan, Values = ["5000"] },
            ],
        };
        var options = Enabled(profile, rule);

        VideoPolicyResolver.Resolve(options, null, null, Facts("h264", 3840, 2160, 7000)).Plan.ProfileId.Should().Be(profile.Id);
        VideoPolicyResolver.Resolve(options, null, null, Facts("h264", 1920, 1080, 4000)).Plan.Action.Should().Be(AdvancedVideoAction.UseSimpleSettings);
    }

    [Fact]
    public void Unknown_numeric_fact_only_matches_is_unknown()
    {
        var profile = LibaomProfile();
        var compare = Rule("compare", VideoRuleField.BitrateKbps, VideoRuleOperator.LessThan, "9000", profile);
        var unknown = Rule("unknown", VideoRuleField.BitrateKbps, VideoRuleOperator.IsUnknown, null, profile);
        var options = Enabled(profile, compare, unknown);

        var facts = new VideoSourceFacts { Codec = "h264", Width = 1920, Height = 1080 };
        var result = VideoPolicyResolver.Resolve(options, null, null, facts);

        result.Plan.RuleName.Should().Be("unknown");
    }

    [Theory]
    [InlineData("yuv420p10le", 10)]
    [InlineData("p010le", 10)]
    [InlineData("p216le", 16)]
    [InlineData("x2rgb10le", 10)]
    [InlineData("v210", 10)]
    [InlineData("yuv420p", 8)]
    public void Source_facts_derive_common_ffmpeg_pixel_format_depths(string pixelFormat, int expectedDepth)
    {
        var facts = VideoSourceFacts.From(new WorkItem { SourcePixelFormat = pixelFormat });

        facts.BitDepth.Should().Be(expectedDepth);
    }

    [Fact]
    public void Folder_can_force_simple_or_a_global_profile()
    {
        var profile = LibaomProfile();
        var options = Enabled(profile);

        var simple = VideoPolicyResolver.Resolve(options,
            new EncoderOptionsOverride { AdvancedVideoPolicy = AdvancedVideoFolderPolicy.Simple }, null, Facts("h264"));
        var forced = VideoPolicyResolver.Resolve(options,
            new EncoderOptionsOverride { AdvancedVideoPolicy = AdvancedVideoFolderPolicy.Profile, AdvancedVideoProfileId = profile.Id }, null, Facts("av1"));

        simple.Plan.Action.Should().Be(AdvancedVideoAction.UseSimpleSettings);
        forced.Plan.ProfileId.Should().Be(profile.Id);
    }

    [Fact]
    public void Exact_encoder_is_not_remapped_by_hardware_preference_override()
    {
        var profile = LibaomProfile();
        var options = Enabled(profile, Rule("all", VideoRuleField.Codec, VideoRuleOperator.IsNot, "never", profile));

        var resolved = VideoPolicyResolver.Resolve(options, null,
            new EncoderOptionsOverride { HardwareAcceleration = "nvidia" }, Facts("h264"));

        resolved.Options.Encoder.Should().Be("libaom-av1");
        resolved.Options.HardwareAcceleration.Should().Be("cpu");
        resolved.Plan.BlockingReason.Should().BeNull();
    }

    [Fact]
    public void Clone_deep_copies_advanced_lists()
    {
        var profile = LibaomProfile();
        profile.CustomOptions.Add(new CustomVideoOption { Option = "-aom-params", Values = ["tune=ssim"] });
        var options = Enabled(profile);

        var clone = options.Clone();
        clone.AdvancedVideo.Profiles[0].CustomOptions[0].Values[0] = "changed";

        options.AdvancedVideo.Profiles[0].CustomOptions[0].Values[0].Should().Be("tune=ssim");
    }

    [Fact]
    public void Malformed_nested_entries_block_without_crashing_the_resolver()
    {
        var options = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [null!],
            },
        };

        var act = () => VideoPolicyResolver.Resolve(options, null, null, Facts("h264"));

        act.Should().NotThrow();
        act().Plan.BlockingReason.Should().Contain("Profile entries must be objects");
    }

    [Fact]
    public void First_enabled_match_wins_and_disabled_rules_are_ignored()
    {
        var first = LibaomProfile(); first.Name = "First";
        var second = LibaomProfile(); second.Name = "Second";
        var disabled = Rule("disabled", VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1", first);
        disabled.Enabled = false;
        var firstEnabled = Rule("first enabled", VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1", second);
        var later = Rule("later", VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1", first);
        var options = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true, Profiles = [first, second], Rules = [disabled, firstEnabled, later],
            },
        };

        VideoPolicyResolver.Resolve(options, null, null, Facts("h264")).Plan.RuleName.Should().Be("first enabled");
    }

    [Fact]
    public void Any_match_supports_codec_aliases_and_inclusive_ranges()
    {
        var rule = new VideoRule
        {
            Match = VideoRuleMatchMode.Any,
            Conditions =
            [
                new() { Field = VideoRuleField.Codec, Operator = VideoRuleOperator.Is, Values = ["H.265"] },
                new() { Field = VideoRuleField.DurationSeconds, Operator = VideoRuleOperator.Between, Values = ["60", "120"] },
            ],
        };

        VideoPolicyResolver.Matches(rule, new VideoSourceFacts { Codec = "hevc", DurationSeconds = 1 }).Should().BeTrue();
        VideoPolicyResolver.Matches(rule, new VideoSourceFacts { Codec = "h264", DurationSeconds = 120 }).Should().BeTrue();
        VideoPolicyResolver.Matches(rule, new VideoSourceFacts { Codec = "h264", DurationSeconds = 121 }).Should().BeFalse();
    }

    [Theory]
    [InlineData(AdvancedVideoAction.Skip)]
    [InlineData(AdvancedVideoAction.MuxOnly)]
    [InlineData(AdvancedVideoAction.UseSimpleSettings)]
    public void Non_profile_actions_resolve_without_a_profile_reference(AdvancedVideoAction action)
    {
        var options = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Rules =
                [
                    new VideoRule
                    {
                        Name = action.ToString(), Action = action,
                        Conditions = [new VideoRuleCondition { Field = VideoRuleField.Codec, Operator = VideoRuleOperator.Is, Values = ["h264"] }],
                    },
                ],
            },
        };

        var result = VideoPolicyResolver.Resolve(options, null, null, Facts("h264"));
        result.Plan.Action.Should().Be(action);
        if (action == AdvancedVideoAction.MuxOnly) result.Options.EncodingMode.Should().Be(EncodingMode.MuxOnly);
    }

    [Fact]
    public void Scalar_codec_conflict_blocks_an_exact_encoder_profile()
    {
        var profile = LibaomProfile();
        var options = Enabled(profile, Rule("all", VideoRuleField.Codec, VideoRuleOperator.IsNot, "never", profile));

        var result = VideoPolicyResolver.Resolve(options,
            new EncoderOptionsOverride { Codec = "h264" }, null, Facts("h265"));

        result.Plan.BlockingReason.Should().Contain("cannot encode");
    }

    [Fact]
    public void Worker_contract_rejects_malformed_advanced_plan_without_reevaluating_rules()
    {
        var options = new EncoderOptions { Codec = "av1", Encoder = "libaom-av1" };
        var plan = new VideoJobPlan
        {
            Action = AdvancedVideoAction.TranscodeWithProfile,
            ExplicitEncoder = "libaom-av1",
            Profile = null,
        };

        VideoPolicyResolver.ValidateResolvedPlan(plan, options)
            .Should().Contain("missing its resolved profile");
    }

    [Fact]
    public void Ordered_static_profiles_cover_1080p_4k_size_and_duration_adaptation()
    {
        var fourK = LibaomProfile(); fourK.Name = "4K slow"; fourK.Preset = "3"; fourK.Threads = 12;
        var hd = LibaomProfile(); hd.Name = "1080p fast"; hd.Preset = "6"; hd.Threads = 6;
        var longFile = LibaomProfile(); longFile.Name = "Long source";
        var options = new EncoderOptions
        {
            AdvancedVideo = new AdvancedVideoOptions
            {
                Enabled = true,
                Profiles = [fourK, hd, longFile],
                Rules =
                [
                    Rule("4K", VideoRuleField.ResolutionClass, VideoRuleOperator.Is, "2160p+", fourK),
                    Rule("1080p", VideoRuleField.ResolutionClass, VideoRuleOperator.Is, "1080p", hd),
                    new VideoRule
                    {
                        Name = "large or long", Match = VideoRuleMatchMode.Any,
                        Action = AdvancedVideoAction.TranscodeWithProfile, ProfileId = longFile.Id,
                        Conditions =
                        [
                            new() { Field = VideoRuleField.FileSizeBytes, Operator = VideoRuleOperator.GreaterThan, Values = ["10000000000"] },
                            new() { Field = VideoRuleField.DurationSeconds, Operator = VideoRuleOperator.GreaterThanOrEqual, Values = ["7200"] },
                        ],
                    },
                ],
            },
        };

        VideoPolicyResolver.Resolve(options, null, null, Facts("h264", 3840, 2160)).Plan.ProfileName.Should().Be("4K slow");
        VideoPolicyResolver.Resolve(options, null, null, Facts("h264", 1920, 1080)).Plan.ProfileName.Should().Be("1080p fast");
        VideoPolicyResolver.Resolve(options, null, null, new VideoSourceFacts
        {
            Codec = "h264", Width = 1280, Height = 720, ResolutionClass = "720p",
            FileSizeBytes = 11_000_000_000, DurationSeconds = 100,
        }).Plan.ProfileName.Should().Be("Long source");
    }

    private static VideoSourceFacts Facts(string codec, int width = 1920, int height = 1080, long? bitrate = 6000) => new()
    {
        Codec = VideoSourceFacts.NormalizeCodec(codec),
        Width = width,
        Height = height,
        ResolutionClass = height >= 2160 ? "2160p+" : "1080p",
        BitrateKbps = bitrate,
        Is4K = width > 1920,
        IsHdr = false,
    };

    private static EncoderOptions Enabled(VideoEncodingProfile profile, params VideoRule[] rules) => new()
    {
        AdvancedVideo = new AdvancedVideoOptions
        {
            Enabled = true,
            Profiles = [profile],
            Rules = [.. rules],
        },
    };

    private static VideoRule Rule(string name, VideoRuleField field, VideoRuleOperator op, string? value, VideoEncodingProfile profile) => new()
    {
        Name = name,
        Action = AdvancedVideoAction.TranscodeWithProfile,
        ProfileId = profile.Id,
        Conditions = [new VideoRuleCondition { Field = field, Operator = op, Values = value == null ? [] : [value] }],
    };

    private static VideoEncodingProfile LibaomProfile() => new()
    {
        Name = "AV1 quality",
        Codec = "av1",
        EncoderSelection = VideoEncoderSelectionMode.Explicit,
        Encoder = "libaom-av1",
        RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = 35 },
        OutputRetention = VideoOutputRetention.AlwaysKeep,
    };
}
