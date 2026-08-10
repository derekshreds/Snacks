using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     The shadow analysis must be conservative: every positive here is a case a
///     user would experience as "my rule silently never fires", and every negative
///     guards against crying wolf on reachable rules.
/// </summary>
public sealed class AdvancedVideoShadowTests
{
    [Fact]
    public void Broader_earlier_codec_rule_shadows_narrower_later_rule()
    {
        var rules = Rules(
            Rule("catch all non-av1", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))),
            Rule("h264 only", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))));

        var shadows = AdvancedVideoRuleAnalysis.FindShadowedRules(rules);

        shadows.Should().ContainSingle().Which.Should().Be(new AdvancedVideoRuleAnalysis.Shadowing(1, 0));
    }

    [Fact]
    public void Codec_aliases_are_normalized_before_comparison()
    {
        var rules = Rules(
            Rule("hevc catcher", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "H.265"))),
            Rule("x265 catcher", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "x265"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules).Should().ContainSingle();
    }

    [Fact]
    public void Disjoint_rules_do_not_shadow()
    {
        var rules = Rules(
            Rule("not av1", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))),
            Rule("av1", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "av1"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules).Should().BeEmpty();
    }

    [Fact]
    public void Narrower_earlier_rule_does_not_shadow_broader_later_rule()
    {
        var rules = Rules(
            Rule("4K non-av1", All(
                Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"),
                Text(VideoRuleField.ResolutionClass, VideoRuleOperator.Is, "2160p+"))),
            Rule("all non-av1", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules).Should().BeEmpty();
    }

    [Fact]
    public void Numeric_range_subset_is_shadowed()
    {
        var rules = Rules(
            Rule("big files", All(Number(VideoRuleField.FileSizeBytes, VideoRuleOperator.GreaterThan, "1000"))),
            Rule("huge files", All(Number(VideoRuleField.FileSizeBytes, VideoRuleOperator.GreaterThanOrEqual, "5000"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules)
            .Should().ContainSingle().Which.RuleIndex.Should().Be(1);
    }

    [Fact]
    public void Numeric_overlap_without_containment_is_not_shadowed()
    {
        var rules = Rules(
            Rule("over 5000", All(Number(VideoRuleField.BitrateKbps, VideoRuleOperator.GreaterThan, "5000"))),
            Rule("4000 to 6000", All(new VideoRuleCondition
            {
                Field = VideoRuleField.BitrateKbps,
                Operator = VideoRuleOperator.Between,
                Values = ["4000", "6000"],
            })));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules).Should().BeEmpty();
    }

    [Fact]
    public void Between_inside_wider_between_is_shadowed()
    {
        var rules = Rules(
            Rule("wide", All(new VideoRuleCondition { Field = VideoRuleField.DurationSeconds, Operator = VideoRuleOperator.Between, Values = ["60", "7200"] })),
            Rule("narrow", All(new VideoRuleCondition { Field = VideoRuleField.DurationSeconds, Operator = VideoRuleOperator.Between, Values = ["600", "1200"] })));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules)
            .Should().ContainSingle().Which.RuleIndex.Should().Be(1);
    }

    [Fact]
    public void In_subset_of_earlier_in_is_shadowed_and_superset_is_not()
    {
        var shadowed = Rules(
            Rule("legacy codecs", All(Text(VideoRuleField.Codec, VideoRuleOperator.In, "h264", "mpeg2video", "vc1"))),
            Rule("h264 or vc1", All(Text(VideoRuleField.Codec, VideoRuleOperator.In, "h264", "vc1"))));
        var reachable = Rules(
            Rule("h264 or vc1", All(Text(VideoRuleField.Codec, VideoRuleOperator.In, "h264", "vc1"))),
            Rule("legacy codecs", All(Text(VideoRuleField.Codec, VideoRuleOperator.In, "h264", "mpeg2video", "vc1"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(shadowed).Should().ContainSingle();
        AdvancedVideoRuleAnalysis.FindShadowedRules(reachable).Should().BeEmpty();
    }

    [Fact]
    public void Any_rule_is_shadowed_only_when_every_disjunct_is_covered()
    {
        var covered = Rules(
            Rule("all non-av1", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))),
            Any_("h264 or hevc",
                Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"),
                Text(VideoRuleField.Codec, VideoRuleOperator.Is, "hevc")));
        var notCovered = Rules(
            Rule("h264 only", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))),
            Any_("h264 or long",
                Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"),
                Number(VideoRuleField.DurationSeconds, VideoRuleOperator.GreaterThan, "7200")));

        AdvancedVideoRuleAnalysis.FindShadowedRules(covered).Should().ContainSingle();
        AdvancedVideoRuleAnalysis.FindShadowedRules(notCovered).Should().BeEmpty();
    }

    [Fact]
    public void Earlier_any_rule_shadows_when_one_alternative_is_implied()
    {
        var rules = Rules(
            Any_("hdr or 4k",
                Bool(VideoRuleField.IsHdr, VideoRuleOperator.Is, "true"),
                Bool(VideoRuleField.Is4K, VideoRuleOperator.Is, "true")),
            Rule("hdr h264", All(
                Bool(VideoRuleField.IsHdr, VideoRuleOperator.Is, "true"),
                Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules)
            .Should().ContainSingle().Which.RuleIndex.Should().Be(1);
    }

    [Fact]
    public void Disabled_earlier_rules_never_shadow_and_disabled_later_rules_are_not_reported()
    {
        var disabledEarlier = Rules(
            Rule("catch all", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1")), enabled: false),
            Rule("h264", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))));
        var disabledLater = Rules(
            Rule("catch all", All(Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1"))),
            Rule("h264", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264")), enabled: false));

        AdvancedVideoRuleAnalysis.FindShadowedRules(disabledEarlier).Should().BeEmpty();
        AdvancedVideoRuleAnalysis.FindShadowedRules(disabledLater).Should().BeEmpty();
    }

    [Fact]
    public void Unconstrained_field_in_later_rule_prevents_shadow_claims()
    {
        // Earlier demands h264; later matches ANY codec over 5000 kb/s — an av1
        // file at 6000 kb/s reaches the later rule, so no shadow.
        var rules = Rules(
            Rule("h264", All(Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264"))),
            Rule("high bitrate", All(Number(VideoRuleField.BitrateKbps, VideoRuleOperator.GreaterThan, "5000"))));

        AdvancedVideoRuleAnalysis.FindShadowedRules(rules).Should().BeEmpty();
    }

    [Fact]
    public void IsKnown_target_is_proven_by_any_value_operator_but_not_vice_versa()
    {
        var shadowed = Rules(
            Rule("bitrate known", All(new VideoRuleCondition { Field = VideoRuleField.BitrateKbps, Operator = VideoRuleOperator.IsKnown, Values = [] })),
            Rule("high bitrate", All(Number(VideoRuleField.BitrateKbps, VideoRuleOperator.GreaterThan, "5000"))));
        var reachable = Rules(
            Rule("high bitrate", All(Number(VideoRuleField.BitrateKbps, VideoRuleOperator.GreaterThan, "5000"))),
            Rule("bitrate known", All(new VideoRuleCondition { Field = VideoRuleField.BitrateKbps, Operator = VideoRuleOperator.IsKnown, Values = [] })));

        AdvancedVideoRuleAnalysis.FindShadowedRules(shadowed).Should().ContainSingle();
        AdvancedVideoRuleAnalysis.FindShadowedRules(reachable).Should().BeEmpty();
    }

    [Fact]
    public void Validator_surfaces_shadowing_as_a_stable_warning()
    {
        var profile = new VideoEncodingProfile
        {
            Name = "AV1",
            Codec = "av1",
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = 32 },
            OutputRetention = VideoOutputRetention.AlwaysKeep,
        };
        var advanced = new AdvancedVideoOptions
        {
            Enabled = true,
            Profiles = [profile],
            Rules =
            [
                new VideoRule
                {
                    Name = "everything", Action = AdvancedVideoAction.TranscodeWithProfile, ProfileId = profile.Id,
                    Conditions = [Text(VideoRuleField.Codec, VideoRuleOperator.IsNot, "av1")],
                },
                new VideoRule
                {
                    Name = "never reached", Action = AdvancedVideoAction.Skip,
                    Conditions = [Text(VideoRuleField.Codec, VideoRuleOperator.Is, "h264")],
                },
            ],
        };

        var result = AdvancedVideoValidator.Validate(advanced);

        result.IsValid.Should().BeTrue("shadowing is a warning, not an error");
        result.Warnings.Should().Contain(d =>
            d.Code == "rule_shadowed" && d.Path == "advancedVideo.rules[1]" && d.Message.Contains("everything"));
    }

    // ------------------------------------------------------------- builders

    private static List<VideoRule> Rules(params VideoRule[] rules) => [.. rules];

    private static VideoRule Rule(string name, List<VideoRuleCondition> conditions, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        Match = VideoRuleMatchMode.All,
        Action = AdvancedVideoAction.Skip,
        Conditions = conditions,
    };

    private static VideoRule Any_(string name, params VideoRuleCondition[] conditions) => new()
    {
        Name = name,
        Match = VideoRuleMatchMode.Any,
        Action = AdvancedVideoAction.Skip,
        Conditions = [.. conditions],
    };

    private static List<VideoRuleCondition> All(params VideoRuleCondition[] conditions) => [.. conditions];

    private static VideoRuleCondition Text(VideoRuleField field, VideoRuleOperator op, params string[] values) =>
        new() { Field = field, Operator = op, Values = [.. values] };

    private static VideoRuleCondition Number(VideoRuleField field, VideoRuleOperator op, string value) =>
        new() { Field = field, Operator = op, Values = [value] };

    private static VideoRuleCondition Bool(VideoRuleField field, VideoRuleOperator op, string value) =>
        new() { Field = field, Operator = op, Values = [value] };
}
