using System.Globalization;
using System.Text.Json.Serialization;
using Snacks.Models;

namespace Snacks.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvancedVideoDiagnosticSeverity { Error, Warning }

public sealed record AdvancedVideoDiagnostic(
    string Path,
    string Code,
    string Message,
    AdvancedVideoDiagnosticSeverity Severity = AdvancedVideoDiagnosticSeverity.Error);

public sealed class AdvancedVideoValidationResult
{
    public List<AdvancedVideoDiagnostic> Diagnostics { get; } = new();
    public bool IsValid => Diagnostics.All(d => d.Severity != AdvancedVideoDiagnosticSeverity.Error);
    public IEnumerable<AdvancedVideoDiagnostic> Errors => Diagnostics.Where(d => d.Severity == AdvancedVideoDiagnosticSeverity.Error);
    public IEnumerable<AdvancedVideoDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == AdvancedVideoDiagnosticSeverity.Warning);
}

/// <summary>Single server-side authority for persisted, previewed, imported, and dispatched advanced settings.</summary>
public static class AdvancedVideoValidator
{
    private static readonly string[] DeniedExact =
    [
        "--", "-i", "-f", "-y", "-n", "-progress", "-report", "-stdin", "-nostdin",
        "-stats", "-nostats", "-loglevel", "-v", "-hide_banner", "-benchmark", "-benchmark_all",
        "-t", "-to", "-ss", "-sseof", "-fs", "-shortest", "-vframes", "-pass",
        "-vf", "-filter:v", "-filter_complex", "-filter_script", "-filter_complex_script",
        "-c", "-codec", "-c:v", "-codec:v", "-vcodec", "-vn", "-an", "-sn", "-dn",
        "-b:a", "-q:a", "-ac", "-ar", "-sample_fmt", "-channel_layout", "-profile:a",
        "-b:s", "-q:s", "-movflags", "-max_muxing_queue_size", "-copyts", "-start_at_zero",
        "-bitexact", "-copy_unknown", "-ignore_unknown", "-re", "-itsoffset", "-lavfi",
    ];

    private static readonly string[] DeniedPrefixes =
    [
        "-map", "-c:a", "-codec:a", "-c:s", "-codec:s", "-c:d", "-codec:d",
        "-b:a:", "-q:a:", "-ac:", "-ar:", "-profile:a:", "-bsf:a", "-filter:a", "-af",
        "-b:s:", "-q:s:", "-bsf:s", "-metadata", "-disposition", "-attach", "-dump_attachment",
        "-passlogfile", "-frames", "-hwaccel", "-init_hw_device", "-filter_hw_device",
        "-protocol_", "-stream_loop", "-readrate", "-hls_", "-segment_", "-dash_",
        "-master_pl_name", "-init_seg_name", "-media_seg_name",
    ];

    private static readonly HashSet<string> TypedVideoOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-b:v", "-minrate", "-maxrate", "-bufsize", "-crf", "-cq", "-qp", "-q:v",
        "-global_quality", "-global_quality:v", "-rc", "-rc_mode", "-preset", "-cpu-used",
        "-speed", "-threads", "-pix_fmt", "-g", "-profile:v", "-level:v",
    };

    private static readonly HashSet<string> InputProducingFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "amovie", "buffer", "abuffer", "cellauto", "frei0r_src", "life",
        "mandelbrot", "mptestsrc", "nullsrc", "openclsrc", "rgbtestsrc", "sierpinski",
        "smptebars", "smptehdbars", "testsrc", "testsrc2", "yuvtestsrc", "anoisesrc",
        "flite", "hilbert", "sine",
    };

    public static AdvancedVideoValidationResult Validate(AdvancedVideoOptions? advanced)
    {
        var result = new AdvancedVideoValidationResult();
        if (advanced == null)
        {
            result.Diagnostics.Add(new("advancedVideo", "required", "AdvancedVideo must be an object."));
            return result;
        }

        if (advanced.Version != 1)
            Error(result, "advancedVideo.version", "schema_version", $"Advanced video schema version {advanced.Version} is not supported; this build supports version 1.");

        var profiles = advanced.Profiles ?? new();
        var rules = advanced.Rules ?? new();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < profiles.Count; i++)
        {
            var path = $"advancedVideo.profiles[{i}]";
            var profile = profiles[i];
            if (profile == null)
            {
                Error(result, path, "profile_required", "Profile entries must be objects.");
                continue;
            }
            if (profile.Id == Guid.Empty || !ids.Add(profile.Id))
                Error(result, $"{path}.id", "profile_id", "Profile IDs must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 80 || !names.Add(profile.Name.Trim()))
                Error(result, $"{path}.name", "profile_name", "Profile names must be 1–80 characters and unique.");

            var codec = VideoSourceFacts.NormalizeCodec(profile.Codec);
            if (codec is not ("h264" or "h265" or "av1"))
                Error(result, $"{path}.codec", "codec", "Profiles support H.264, H.265, or AV1.");

            VideoEncoderRegistry.Descriptor descriptor;
            if (profile.EncoderSelection == VideoEncoderSelectionMode.Explicit)
            {
                if (string.IsNullOrWhiteSpace(profile.Encoder))
                {
                    Error(result, $"{path}.encoder", "encoder_required", "Exact encoder selection requires an FFmpeg encoder name.");
                    descriptor = VideoEncoderRegistry.Describe("", codec);
                }
                else
                {
                    descriptor = VideoEncoderRegistry.Describe(profile.Encoder, codec);
                    var encoderCodec = VideoEncoderRegistry.EncoderCodec(profile.Encoder);
                    if (encoderCodec != null && encoderCodec != codec)
                        Error(result, $"{path}.encoder", "encoder_codec", $"{profile.Encoder} encodes {encoderCodec}, not {codec}.");
                    if (descriptor.Family == "custom")
                        Warn(result, $"{path}.encoder", "encoder_unavailable", "This encoder is not in the known adapter registry; availability is checked at dispatch and Custom rate control is required.");
                }
            }
            else
            {
                descriptor = VideoEncoderRegistry.Describe(VideoEncoderRegistry.DefaultSoftwareEncoder(codec), codec);
            }

            if (profile.RateControl == null)
                Error(result, $"{path}.rateControl", "rate_control_required", "Rate control must be an object.");
            var rc = profile.RateControl ?? new VideoRateControlOptions();
            if (descriptor.Family == "custom" && rc.Mode != VideoRateControlMode.Custom)
                Error(result, $"{path}.rateControl.mode", "rate_control_adapter", "Detected encoders without a known adapter must use Custom rate control.");
            if (rc.Mode == VideoRateControlMode.Bitrate && rc.TargetKbps <= 0)
                Error(result, $"{path}.rateControl.targetKbps", "bitrate", "Target bitrate must be greater than zero.");
            if (rc.MinKbps < 0 || rc.MaxKbps < 0 || rc.BufferKbits < 0)
                Error(result, $"{path}.rateControl", "bitrate_range", "Rate limits cannot be negative.");
            if (rc.MinKbps > 0 && rc.MaxKbps > 0 && rc.MinKbps > rc.MaxKbps)
                Error(result, $"{path}.rateControl", "bitrate_order", "Minimum bitrate cannot exceed maximum bitrate.");
            if (rc.Mode == VideoRateControlMode.Quality)
            {
                if (!descriptor.SupportsTypedQuality)
                    Error(result, $"{path}.rateControl.mode", "quality_unsupported", "This encoder has no typed quality adapter; select Custom mode.");
                else if (rc.Quality < descriptor.QualityMin || rc.Quality > descriptor.QualityMax)
                    Error(result, $"{path}.rateControl.quality", "quality_range", $"{descriptor.QualityLabel} must be between {descriptor.QualityMin} and {descriptor.QualityMax}.");
                if (profile.OutputRetention == VideoOutputRetention.SmallerOnly)
                    Warn(result, $"{path}.outputRetention", "quality_smaller_only", "Quality-based output may be larger and will be discarded under SmallerOnly.");
                if (!descriptor.SupportsQualityConstraints && (rc.MaxKbps > 0 || rc.BufferKbits > 0))
                    Warn(result, $"{path}.rateControl", "quality_constraint_unsupported", $"{descriptor.Family} quality mode does not support max-rate constraints; max/buffer values will be ignored.");
            }
            if (profile.Threads < 0 || profile.GopSize < 0 || profile.MaxFrameRate < 0)
                Error(result, path, "negative_value", "Threads, GOP, and frame-rate values cannot be negative.");

            ValidateFilters(result, profile, path);
            ValidateCustomOptions(result, profile, path);
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var path = $"advancedVideo.rules[{i}]";
            var rule = rules[i];
            if (rule == null)
            {
                Error(result, path, "rule_required", "Rule entries must be objects.");
                continue;
            }
            if (rule.Id == Guid.Empty || !ids.Add(rule.Id))
                Error(result, $"{path}.id", "rule_id", "Rule IDs must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 80)
                Error(result, $"{path}.name", "rule_name", "Rule names must be 1–80 characters.");
            if (rule.Conditions == null || rule.Conditions.Count == 0)
                Error(result, $"{path}.conditions", "conditions_required", "A rule needs at least one condition.");
            else
                for (var j = 0; j < rule.Conditions.Count; j++)
                {
                    if (rule.Conditions[j] == null)
                        Error(result, $"{path}.conditions[{j}]", "condition_required", "Condition entries must be objects.");
                    else
                        ValidateCondition(result, rule.Conditions[j], $"{path}.conditions[{j}]");
                }
            ValidateProfileReference(result, profiles, rule.Action, rule.ProfileId, $"{path}.profileId");
        }

        foreach (var shadow in AdvancedVideoRuleAnalysis.FindShadowedRules(rules))
            Warn(result, $"advancedVideo.rules[{shadow.RuleIndex}]", "rule_shadowed",
                $"\"{rules[shadow.RuleIndex]!.Name}\" can never match — \"{rules[shadow.ByRuleIndex]!.Name}\" always claims those files first. Reorder or tighten the earlier rule.");

        ValidateProfileReference(result, profiles, advanced.DefaultAction, advanced.DefaultProfileId, "advancedVideo.defaultProfileId");
        return result;
    }

    private static void ValidateCondition(AdvancedVideoValidationResult result, VideoRuleCondition condition, string path)
    {
        bool numeric = condition.Field is VideoRuleField.Width or VideoRuleField.Height or VideoRuleField.BitrateKbps
            or VideoRuleField.FileSizeBytes or VideoRuleField.DurationSeconds or VideoRuleField.BitDepth;
        bool boolean = condition.Field is VideoRuleField.IsHdr or VideoRuleField.Is4K;
        bool text = !numeric && !boolean;

        bool operatorAllowed = condition.Operator switch
        {
            VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown => true,
            VideoRuleOperator.Is or VideoRuleOperator.IsNot          => true,
            VideoRuleOperator.In or VideoRuleOperator.NotIn          => text || numeric,
            VideoRuleOperator.GreaterThan or VideoRuleOperator.GreaterThanOrEqual
                or VideoRuleOperator.LessThan or VideoRuleOperator.LessThanOrEqual
                or VideoRuleOperator.Between                         => numeric,
            _ => false,
        };
        if (!operatorAllowed)
            Error(result, $"{path}.operator", "condition_operator", $"{condition.Operator} is not supported for {condition.Field}.");

        int required = condition.Operator switch
        {
            VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown => 0,
            VideoRuleOperator.Between => 2,
            _ => 1,
        };
        int count = condition.Values?.Count ?? 0;
        if (count < required)
            Error(result, $"{path}.values", "condition_values", $"{condition.Operator} requires {required} value(s).");
        else if (condition.Operator is VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown && count != 0)
            Error(result, $"{path}.values", "condition_arity", $"{condition.Operator} does not accept values.");
        else if (condition.Operator == VideoRuleOperator.Between && count != 2)
            Error(result, $"{path}.values", "condition_arity", "Between requires exactly two values.");
        else if (condition.Operator is not (VideoRuleOperator.In or VideoRuleOperator.NotIn
                                             or VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown
                                             or VideoRuleOperator.Between) && count != 1)
            Error(result, $"{path}.values", "condition_arity", $"{condition.Operator} requires exactly one value.");

        if (numeric && condition.Operator is not (VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown))
        {
            foreach (var value in condition.Values ?? [])
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    Error(result, $"{path}.values", "numeric_value", $"'{value}' is not an invariant numeric value.");
        }
        if (boolean && condition.Operator is not (VideoRuleOperator.IsKnown or VideoRuleOperator.IsUnknown))
        {
            foreach (var value in condition.Values ?? [])
                if (!bool.TryParse(value, out _))
                    Error(result, $"{path}.values", "boolean_value", $"'{value}' must be true or false.");
        }
    }

    private static void ValidateProfileReference(AdvancedVideoValidationResult result, List<VideoEncodingProfile> profiles,
        AdvancedVideoAction action, Guid? profileId, string path)
    {
        if (action != AdvancedVideoAction.TranscodeWithProfile) return;
        if (!profileId.HasValue || profiles.All(p => p == null || p.Id != profileId.Value))
            Error(result, path, "profile_reference", "TranscodeWithProfile requires an existing profile.");
    }

    private static void ValidateFilters(AdvancedVideoValidationResult result, VideoEncodingProfile profile, string path)
    {
        var filters = profile.AdditionalVideoFilters ?? new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i]?.Trim() ?? "";
            if (filter.Length == 0)
                Error(result, $"{path}.additionalVideoFilters[{i}]", "filter_empty", "Filters cannot be empty.");
            if (filter.Contains(';') || filter.Contains('[') || filter.Contains(']'))
                Error(result, $"{path}.additionalVideoFilters[{i}]", "filter_topology", "Only a single ordered video-filter chain is allowed.");
            foreach (var segment in SplitUnescaped(filter, ','))
            {
                var head = segment.Trim().Split(['=', ':'], StringSplitOptions.TrimEntries)[0];
                if (InputProducingFilters.Contains(head)
                    || head.Equals("zmq", StringComparison.OrdinalIgnoreCase)
                    || head.Equals("sendcmd", StringComparison.OrdinalIgnoreCase))
                    Error(result, $"{path}.additionalVideoFilters[{i}]", "filter_source", $"The {head} filter is not allowed in the guarded video chain.");
            }
        }
    }

    private static IEnumerable<string> SplitUnescaped(string value, char separator)
    {
        var start = 0;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (value[i] == '\\') { escaped = true; continue; }
            if (value[i] != separator) continue;
            yield return value[start..i];
            start = i + 1;
        }
        yield return value[start..];
    }

    private static void ValidateCustomOptions(AdvancedVideoValidationResult result, VideoEncodingProfile profile, string path)
    {
        var options = profile.CustomOptions ?? new List<CustomVideoOption>();
        for (var i = 0; i < options.Count; i++)
        {
            var optionPath = $"{path}.customOptions[{i}]";
            var custom = options[i] ?? new CustomVideoOption();
            var option = custom.Option?.Trim() ?? "";
            if (!option.StartsWith('-') || option.Length < 2 || option.Any(char.IsWhiteSpace) || option.Contains('\0'))
            {
                Error(result, $"{optionPath}.option", "option_shape", "A custom entry must contain one FFmpeg option token beginning with '-'.");
                continue;
            }
            if (DeniedExact.Contains(option, StringComparer.OrdinalIgnoreCase)
                || DeniedPrefixes.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                Error(result, $"{optionPath}.option", "option_reserved", $"{option} controls command structure and is reserved by Snacks.");
            if ((custom.Values ?? new List<string>()).Any(v => v.Contains('\0') || v.Contains('\r') || v.Contains('\n')))
                Error(result, $"{optionPath}.values", "option_value", "Custom option values cannot contain NUL or newlines.");
            if ((custom.Values?.Count ?? 0) > 1)
                Error(result, $"{optionPath}.values", "option_arity", "A guarded custom option accepts at most one literal value token; repeat the option for additional values.");
            foreach (var value in custom.Values ?? [])
            {
                var token = value.Trim();
                if (DeniedExact.Contains(token, StringComparer.OrdinalIgnoreCase)
                    || DeniedPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    Error(result, $"{optionPath}.values", "option_value_reserved", $"{token} cannot be smuggled through an option value.");
            }
            if (TypedVideoOptions.Contains(option))
                Warn(result, $"{optionPath}.option", "option_override", $"{option} overrides a typed profile setting because custom options are appended last.");
        }
    }

    private static void Error(AdvancedVideoValidationResult result, string path, string code, string message) =>
        result.Diagnostics.Add(new(path, code, message));
    private static void Warn(AdvancedVideoValidationResult result, string path, string code, string message) =>
        result.Diagnostics.Add(new(path, code, message, AdvancedVideoDiagnosticSeverity.Warning));
}
