using System.Globalization;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Pure source-policy resolver shared by scanning, dry-run analysis, re-evaluation,
///     local scheduling, and cluster candidate selection.
/// </summary>
public static class VideoPolicyResolver
{
    /// <summary>
    ///     Validates the resolved coordinator-to-worker contract without evaluating
    ///     any rules. Workers call this at receipt time and execute only the supplied
    ///     effective options/plan when it succeeds.
    /// </summary>
    public static string? ValidateResolvedPlan(VideoJobPlan? plan, EncoderOptions options)
    {
        if (plan == null || !plan.IsAdvanced) return null;
        if (plan.ProtocolVersion != VideoJobPlan.CurrentProtocolVersion)
            return $"Advanced video protocol {plan.ProtocolVersion} is not supported; expected {VideoJobPlan.CurrentProtocolVersion}.";
        if (!string.IsNullOrWhiteSpace(plan.BlockingReason))
            return $"The coordinator sent a blocked advanced video plan: {plan.BlockingReason}";
        if (plan.Action == AdvancedVideoAction.Skip)
            return "A Skip policy action must not be dispatched to a worker.";
        if (plan.Action == AdvancedVideoAction.MuxOnly && options.EncodingMode != EncodingMode.MuxOnly)
            return "MuxOnly plan requires effective EncodingMode.MuxOnly options.";
        if (plan.Action != AdvancedVideoAction.TranscodeWithProfile) return null;
        if (plan.Profile == null)
            return "TranscodeWithProfile plan is missing its resolved profile.";
        if (plan.ProfileId.HasValue && plan.Profile.Id != plan.ProfileId.Value)
            return "Resolved profile ID does not match the plan profile reference.";
        if (plan.Profile.EncoderSelection == VideoEncoderSelectionMode.Explicit)
        {
            if (string.IsNullOrWhiteSpace(plan.ExplicitEncoder)
                || !string.Equals(plan.Profile.Encoder, plan.ExplicitEncoder, StringComparison.OrdinalIgnoreCase))
                return "Resolved exact encoder does not match the selected profile.";
            if (!string.Equals(options.Encoder, plan.ExplicitEncoder, StringComparison.OrdinalIgnoreCase))
                return "Effective encoder does not match the exact encoder in the resolved plan.";
            var codec = VideoEncoderRegistry.EncoderCodec(plan.ExplicitEncoder);
            if (codec != null && codec != VideoSourceFacts.NormalizeCodec(options.Codec))
                return $"Exact encoder {plan.ExplicitEncoder} cannot encode the effective {options.Codec} codec.";
        }
        return null;
    }

    public static VideoPolicyResolution Resolve(
        EncoderOptions global,
        EncoderOptionsOverride? folderOverride,
        EncoderOptionsOverride? nodeOverride,
        VideoSourceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(facts);

        var advanced = global.AdvancedVideo ?? new AdvancedVideoOptions();
        if (!advanced.Enabled || folderOverride?.AdvancedVideoPolicy == AdvancedVideoFolderPolicy.Simple)
            return Simple(global, folderOverride, nodeOverride);

        var validation = AdvancedVideoValidator.Validate(advanced);
        var plan = new VideoJobPlan
        {
            Warnings = validation.Warnings.Select(d => d.Message).Distinct().ToList(),
        };

        if (!validation.IsValid)
        {
            plan.BlockingReason = string.Join(" ", validation.Errors.Select(d => d.Message).Distinct());
            return new VideoPolicyResolution
            {
                Options = EncoderOptionsOverride.ApplyOverrides(global, folderOverride, nodeOverride),
                Plan    = plan,
            };
        }

        var selection = Select(advanced, folderOverride, facts);
        plan.Action   = selection.Action;
        plan.RuleId   = selection.Rule?.Id;
        plan.RuleName = selection.Rule?.Name;

        if (selection.Action == AdvancedVideoAction.UseSimpleSettings)
        {
            var simple = Simple(global, folderOverride, nodeOverride);
            simple.Plan.RuleId   = selection.Rule?.Id;
            simple.Plan.RuleName = selection.Rule?.Name;
            return simple;
        }

        if (selection.Action == AdvancedVideoAction.Skip)
            return new VideoPolicyResolution
            {
                Options = EncoderOptionsOverride.ApplyOverrides(global, folderOverride, nodeOverride),
                Plan    = plan,
            };

        if (selection.Action == AdvancedVideoAction.MuxOnly)
        {
            var muxOptions = EncoderOptionsOverride.ApplyOverrides(global, folderOverride, nodeOverride);
            muxOptions.EncodingMode = EncodingMode.MuxOnly;
            return new VideoPolicyResolution { Options = muxOptions, Plan = plan };
        }

        var profile = selection.Profile;
        if (profile == null)
        {
            plan.BlockingReason = "The selected advanced action references a missing video profile.";
            return new VideoPolicyResolution
            {
                Options = EncoderOptionsOverride.ApplyOverrides(global, folderOverride, nodeOverride),
                Plan    = plan,
            };
        }

        var profiled = ApplyProfile(global, profile);
        var effective = EncoderOptionsOverride.ApplyOverrides(profiled, folderOverride, nodeOverride);
        effective.EncodingMode = EncodingMode.Transcode;

        var normalizedCodec = VideoSourceFacts.NormalizeCodec(effective.Codec);
        if (string.Equals(effective.Format, "webm", StringComparison.OrdinalIgnoreCase))
        {
            normalizedCodec = "av1";
            effective.Codec = "av1";
            if (profile.EncoderSelection == VideoEncoderSelectionMode.Automatic)
                effective.Encoder = VideoEncoderRegistry.DefaultSoftwareEncoder("av1");
        }

        string? explicitEncoder = null;
        if (profile.EncoderSelection == VideoEncoderSelectionMode.Explicit)
        {
            explicitEncoder = profile.Encoder?.Trim();
            effective.Encoder = explicitEncoder ?? effective.Encoder;
            effective.HardwareAcceleration = VideoEncoderRegistry.InferDevice(effective.Encoder);

            if ((folderOverride?.Encoder != null && !string.Equals(folderOverride.Encoder, explicitEncoder, StringComparison.OrdinalIgnoreCase))
                || (nodeOverride?.Encoder != null && !string.Equals(nodeOverride.Encoder, explicitEncoder, StringComparison.OrdinalIgnoreCase)))
                plan.BlockingReason = "A folder or node encoder override conflicts with the exact encoder selected by the profile.";

            var encoderCodec = VideoEncoderRegistry.EncoderCodec(effective.Encoder);
            if (encoderCodec != null && encoderCodec != normalizedCodec)
                plan.BlockingReason = $"Exact encoder {effective.Encoder} cannot encode the effective {normalizedCodec} codec.";
        }

        plan.ProfileId       = profile.Id;
        plan.ProfileName     = profile.Name;
        plan.Profile         = profile.Clone();
        plan.ExplicitEncoder = explicitEncoder;
        plan.OutputRetention = profile.OutputRetention;

        return new VideoPolicyResolution { Options = effective, Plan = plan };
    }

    private static VideoPolicyResolution Simple(EncoderOptions global, EncoderOptionsOverride? folder, EncoderOptionsOverride? node) =>
        new()
        {
            Options = EncoderOptionsOverride.ApplyOverrides(global, folder, node),
            Plan = new VideoJobPlan
            {
                Action          = AdvancedVideoAction.UseSimpleSettings,
                OutputRetention = VideoOutputRetention.SmallerOnly,
            },
        };

    private static (AdvancedVideoAction Action, VideoRule? Rule, VideoEncodingProfile? Profile) Select(
        AdvancedVideoOptions advanced, EncoderOptionsOverride? folder, VideoSourceFacts facts)
    {
        if (folder?.AdvancedVideoPolicy == AdvancedVideoFolderPolicy.Profile)
        {
            var forced = advanced.Profiles.FirstOrDefault(p => p.Id == folder.AdvancedVideoProfileId);
            return (AdvancedVideoAction.TranscodeWithProfile, null, forced);
        }

        foreach (var rule in advanced.Rules.Where(r => r.Enabled))
        {
            if (!Matches(rule, facts)) continue;
            var profile = rule.Action == AdvancedVideoAction.TranscodeWithProfile
                ? advanced.Profiles.FirstOrDefault(p => p.Id == rule.ProfileId)
                : null;
            return (rule.Action, rule, profile);
        }

        var defaultProfile = advanced.DefaultAction == AdvancedVideoAction.TranscodeWithProfile
            ? advanced.Profiles.FirstOrDefault(p => p.Id == advanced.DefaultProfileId)
            : null;
        return (advanced.DefaultAction, null, defaultProfile);
    }

    public static bool Matches(VideoRule rule, VideoSourceFacts facts)
    {
        if (rule.Conditions is not { Count: > 0 }) return false;
        return rule.Match == VideoRuleMatchMode.All
            ? rule.Conditions.All(c => Matches(c, facts))
            : rule.Conditions.Any(c => Matches(c, facts));
    }

    public static bool Matches(VideoRuleCondition condition, VideoSourceFacts facts)
    {
        object? actual = condition.Field switch
        {
            VideoRuleField.Codec            => facts.Codec,
            VideoRuleField.Width            => facts.Width,
            VideoRuleField.Height           => facts.Height,
            VideoRuleField.ResolutionClass  => facts.ResolutionClass,
            VideoRuleField.BitrateKbps      => facts.BitrateKbps,
            VideoRuleField.FileSizeBytes    => facts.FileSizeBytes,
            VideoRuleField.DurationSeconds  => facts.DurationSeconds,
            VideoRuleField.PixelFormat      => facts.PixelFormat,
            VideoRuleField.BitDepth         => facts.BitDepth,
            VideoRuleField.IsHdr            => facts.IsHdr,
            VideoRuleField.Is4K             => facts.Is4K,
            _ => null,
        };

        if (condition.Operator == VideoRuleOperator.IsKnown) return actual != null;
        if (condition.Operator == VideoRuleOperator.IsUnknown) return actual == null;
        if (actual == null) return false;

        var values = condition.Values ?? new();
        if (actual is bool boolean)
        {
            if (values.Count == 0 || !bool.TryParse(values[0], out var expected)) return false;
            return condition.Operator switch
            {
                VideoRuleOperator.Is    => boolean == expected,
                VideoRuleOperator.IsNot => boolean != expected,
                _ => false,
            };
        }

        if (actual is int or long or double)
        {
            var number = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
            var parsed = values.Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (double?)n : null).ToList();
            if (parsed.Count == 0 || parsed[0] == null) return false;
            return condition.Operator switch
            {
                VideoRuleOperator.Is                 => number == parsed[0],
                VideoRuleOperator.IsNot              => number != parsed[0],
                VideoRuleOperator.In                 => parsed.Where(v => v.HasValue).Any(v => number == v),
                VideoRuleOperator.NotIn              => parsed.Where(v => v.HasValue).All(v => number != v),
                VideoRuleOperator.GreaterThan        => number > parsed[0],
                VideoRuleOperator.GreaterThanOrEqual => number >= parsed[0],
                VideoRuleOperator.LessThan           => number < parsed[0],
                VideoRuleOperator.LessThanOrEqual    => number <= parsed[0],
                VideoRuleOperator.Between when parsed.Count > 1 && parsed[1].HasValue
                    => number >= Math.Min(parsed[0]!.Value, parsed[1]!.Value)
                       && number <= Math.Max(parsed[0]!.Value, parsed[1]!.Value),
                _ => false,
            };
        }

        var text = actual.ToString()?.Trim().ToLowerInvariant() ?? "";
        if (condition.Field == VideoRuleField.Codec) text = VideoSourceFacts.NormalizeCodec(text);
        var normalizedValues = values.Select(v => condition.Field == VideoRuleField.Codec
                ? VideoSourceFacts.NormalizeCodec(v)
                : v.Trim().ToLowerInvariant())
            .ToList();
        return condition.Operator switch
        {
            VideoRuleOperator.Is    => normalizedValues.Count > 0 && text == normalizedValues[0],
            VideoRuleOperator.IsNot => normalizedValues.Count > 0 && text != normalizedValues[0],
            VideoRuleOperator.In    => normalizedValues.Contains(text),
            VideoRuleOperator.NotIn => !normalizedValues.Contains(text),
            _ => false,
        };
    }

    private static EncoderOptions ApplyProfile(EncoderOptions global, VideoEncodingProfile profile)
    {
        var result = global.Clone();
        result.Codec = VideoSourceFacts.NormalizeCodec(profile.Codec);
        result.Encoder = profile.EncoderSelection == VideoEncoderSelectionMode.Explicit
            ? profile.Encoder ?? VideoEncoderRegistry.DefaultSoftwareEncoder(result.Codec)
            : VideoEncoderRegistry.DefaultSoftwareEncoder(result.Codec);
        result.HardwareAcceleration = profile.EncoderSelection == VideoEncoderSelectionMode.Explicit
            ? VideoEncoderRegistry.InferDevice(result.Encoder)
            : profile.HardwareAcceleration;
        if (profile.RateControl.Mode == VideoRateControlMode.Bitrate)
        {
            result.TargetBitrate = profile.RateControl.TargetKbps;
            result.StrictBitrate = profile.RateControl.StrictBitrate;
        }
        result.FfmpegQualityPreset = profile.Preset ?? result.FfmpegQualityPreset;
        result.VideoProfile        = profile.VideoProfile;
        result.VideoLevel          = profile.VideoLevel;
        result.DownscalePolicy     = profile.DownscalePolicy;
        result.DownscaleTarget     = profile.DownscaleTarget;
        result.FixedFrameSize      = profile.FixedFrameSize;
        result.MaxFrameRate        = profile.MaxFrameRate;
        result.TonemapHdrToSdr     = profile.TonemapHdrToSdr;
        result.RemoveBlackBorders  = profile.RemoveBlackBorders;
        return result;
    }
}
