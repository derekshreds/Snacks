using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Aggregates what a candidate Advanced Video policy would decide for every
///     tracked video file, using the same <see cref="VideoPolicyResolver"/> the
///     scan and dispatch paths run. Pure over its inputs so the settings UI can
///     preview a staged, unsaved policy without side effects.
/// </summary>
public static class AdvancedVideoImpactService
{
    public sealed record Sample(string FileName, string? Codec, int? Width, int? Height, string? RuleName);

    public sealed record Bucket
    {
        public required string Key { get; init; }
        public AdvancedVideoAction Action { get; init; }
        public bool Blocked { get; init; }
        public Guid? ProfileId { get; init; }
        public string? ProfileName { get; init; }
        public string? BlockingReason { get; init; }
        public List<string> RuleNames { get; init; } = new();
        public int Count { get; init; }
        public List<Sample> Samples { get; init; } = new();

        /// <summary>Bytes these files occupy today.</summary>
        public long TotalBytes { get; init; }

        /// <summary>
        ///     Files in this bucket already settled (Completed or Skipped). Applying
        ///     a policy never reprocesses them by itself — Re-evaluate does — so the
        ///     UI shows this to keep "would happen" honest about "will queue".
        /// </summary>
        public int AlreadyProcessedCount { get; init; }

        /// <summary>
        ///     Forecast size after encoding — only for bitrate-mode profile buckets,
        ///     where target × duration is honest arithmetic. Quality and custom rate
        ///     control are deliberately unforecast: their size depends on content.
        /// </summary>
        public long? ProjectedBytes { get; init; }
    }

    public sealed record Result
    {
        public int Analyzed { get; init; }
        public List<Bucket> Buckets { get; init; } = new();

        /// <summary>Files caught per rule (key: rule id), for per-card badges.</summary>
        public Dictionary<Guid, int> RuleCounts { get; init; } = new();

        /// <summary>Files no enabled rule matched — the default action's audience.</summary>
        public int UnmatchedCount { get; init; }
    }

    /// <summary>
    ///     Buckets are keyed by outcome: blocked plans by their reason, profile
    ///     transcodes by profile, everything else by action. Node overrides are a
    ///     dispatch-time concern and deliberately excluded from the preview.
    /// </summary>
    public static Result Aggregate(
        EncoderOptions candidate,
        IEnumerable<(MediaFile File, EncoderOptionsOverride? Folder)> rows,
        int sampleLimit = 8)
    {
        var accumulators = new Dictionary<string, MutableBucket>();
        var ruleCounts = new Dictionary<Guid, int>();
        var analyzed = 0;
        var unmatched = 0;

        foreach (var (file, folder) in rows)
        {
            analyzed++;
            var plan = VideoPolicyResolver.Resolve(candidate, folder, null, VideoSourceFacts.From(file)).Plan;
            var blocked = !string.IsNullOrEmpty(plan.BlockingReason);
            if (plan.RuleId is { } ruleId) ruleCounts[ruleId] = ruleCounts.GetValueOrDefault(ruleId) + 1;
            else if (!blocked) unmatched++;
            var key = blocked ? $"blocked:{plan.BlockingReason}"
                : plan.Action == AdvancedVideoAction.TranscodeWithProfile ? $"profile:{plan.ProfileId}"
                : $"action:{plan.Action}";

            if (!accumulators.TryGetValue(key, out var bucket))
            {
                bucket = new MutableBucket
                {
                    Key            = key,
                    Action         = plan.Action,
                    Blocked        = blocked,
                    ProfileId      = plan.ProfileId,
                    ProfileName    = plan.ProfileName,
                    BlockingReason = plan.BlockingReason,
                };
                accumulators[key] = bucket;
            }

            bucket.Count++;
            if (file.Status is MediaFileStatus.Completed or MediaFileStatus.Skipped) bucket.AlreadyProcessedCount++;
            if (file.FileSize > 0) bucket.TotalBytes += file.FileSize;
            var rc = plan.Profile?.RateControl;
            if (!blocked && plan.Action == AdvancedVideoAction.TranscodeWithProfile
                && rc is { Mode: VideoRateControlMode.Bitrate } && rc.TargetKbps > 0 && file.Duration > 0)
                bucket.ProjectedBytes += (long)(rc.TargetKbps * 125.0 * file.Duration);
            if (plan.RuleName != null) bucket.RuleNames.Add(plan.RuleName);
            if (bucket.Samples.Count < sampleLimit)
                bucket.Samples.Add(new Sample(file.FileName, file.Codec, file.Width, file.Height, plan.RuleName));
        }

        return new Result
        {
            Analyzed = analyzed,
            RuleCounts = ruleCounts,
            UnmatchedCount = unmatched,
            // Fully deterministic order: ties on count and size fall back to the
            // human-visible label, never to the run-random profile guid in Key.
            Buckets = accumulators.Values
                .OrderByDescending(bucket => bucket.Count)
                .ThenByDescending(bucket => bucket.TotalBytes)
                .ThenBy(bucket => bucket.ProfileName ?? bucket.BlockingReason ?? bucket.Action.ToString(),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
                .Select(bucket => new Bucket
                {
                    Key            = bucket.Key,
                    Action         = bucket.Action,
                    Blocked        = bucket.Blocked,
                    ProfileId      = bucket.ProfileId,
                    ProfileName    = bucket.ProfileName,
                    BlockingReason = bucket.BlockingReason,
                    RuleNames      = bucket.RuleNames.Distinct().ToList(),
                    Count          = bucket.Count,
                    Samples        = bucket.Samples,
                    TotalBytes     = bucket.TotalBytes,
                    ProjectedBytes = bucket.ProjectedBytes > 0 ? bucket.ProjectedBytes : null,
                    AlreadyProcessedCount = bucket.AlreadyProcessedCount,
                })
                .ToList(),
        };
    }

    private sealed class MutableBucket
    {
        public required string Key;
        public AdvancedVideoAction Action;
        public bool Blocked;
        public Guid? ProfileId;
        public string? ProfileName;
        public string? BlockingReason;
        public readonly List<string> RuleNames = new();
        public int Count;
        public long TotalBytes;
        public long ProjectedBytes;
        public int AlreadyProcessedCount;
        public readonly List<Sample> Samples = new();
    }

    public sealed record FileMatch(
        string FileName, AdvancedVideoAction Action, bool Blocked,
        string? ProfileName, string? RuleName, string? BlockingReason);

    /// <summary>
    ///     Answers "what happens to *this* file?" — resolves every row whose name
    ///     contains the query and reports its decision. Same resolver, same folder
    ///     overrides, read-only.
    /// </summary>
    public static List<FileMatch> FindFiles(
        EncoderOptions candidate,
        IEnumerable<(MediaFile File, EncoderOptionsOverride? Folder)> rows,
        string query,
        int limit = 5)
    {
        var matches = new List<FileMatch>();
        var needle = query.Trim();
        if (needle.Length == 0) return matches;

        foreach (var (file, folder) in rows)
        {
            if (!file.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            var plan = VideoPolicyResolver.Resolve(candidate, folder, null, VideoSourceFacts.From(file)).Plan;
            matches.Add(new FileMatch(
                file.FileName, plan.Action, !string.IsNullOrEmpty(plan.BlockingReason),
                plan.ProfileName, plan.RuleName, plan.BlockingReason));
            if (matches.Count >= limit) break;
        }
        return matches;
    }
}
