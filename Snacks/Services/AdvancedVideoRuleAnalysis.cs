using System.Globalization;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Static reachability analysis over the ordered rule list: a rule is
///     "shadowed" when every source that could match it is already claimed by an
///     earlier enabled rule, so it can never fire. Mirrors the exact semantics of
///     <see cref="VideoPolicyResolver.Matches(VideoRuleCondition, VideoSourceFacts)"/>
///     (unknown facts match only IsUnknown; codec aliases normalized; numeric
///     compare as double) and is deliberately conservative: it only reports a
///     shadow it can prove, never guesses.
/// </summary>
public static class AdvancedVideoRuleAnalysis
{
    public sealed record Shadowing(int RuleIndex, int ByRuleIndex);

    public static List<Shadowing> FindShadowedRules(IReadOnlyList<VideoRule?> rules)
    {
        var result = new List<Shadowing>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule is not { Enabled: true } || rule.Conditions is not { Count: > 0 }) continue;
            for (var j = 0; j < i; j++)
            {
                var earlier = rules[j];
                if (earlier is not { Enabled: true } || earlier.Conditions is not { Count: > 0 }) continue;
                if (!IsSubsumed(rule, earlier)) continue;
                result.Add(new Shadowing(i, j));
                break;
            }
        }
        return result;
    }

    /// <summary>match(rule) ⊆ match(earlier)?</summary>
    private static bool IsSubsumed(VideoRule rule, VideoRule earlier)
    {
        // An Any-rule is a union of single-condition rules; each disjunct must be covered.
        var conjunctions = rule.Match == VideoRuleMatchMode.Any
            ? rule.Conditions.Where(c => c != null).Select(c => new List<VideoRuleCondition> { c }).ToList()
            : [rule.Conditions.Where(c => c != null).ToList()];
        if (conjunctions.Count == 0 || conjunctions.Any(c => c.Count == 0)) return false;

        return conjunctions.All(conjunction =>
            earlier.Match == VideoRuleMatchMode.All
                ? earlier.Conditions.All(target => target != null && Implies(conjunction, target))
                : earlier.Conditions.Any(target => target != null && Implies(conjunction, target)));
    }

    /// <summary>Does the conjunction guarantee the target condition is true?</summary>
    private static bool Implies(List<VideoRuleCondition> conjunction, VideoRuleCondition target)
    {
        var onField = conjunction.Where(c => c.Field == target.Field).ToList();
        if (onField.Count == 0) return false; // Unconstrained field can be anything, including unknown.

        var requiresKnown = onField.Any(c => c.Operator != VideoRuleOperator.IsUnknown);
        var requiresUnknown = onField.Any(c => c.Operator == VideoRuleOperator.IsUnknown);
        if (requiresKnown && requiresUnknown) return false; // Contradiction: never claim anything.

        if (target.Operator == VideoRuleOperator.IsKnown) return requiresKnown;
        if (target.Operator == VideoRuleOperator.IsUnknown) return requiresUnknown;
        if (requiresUnknown) return false; // Unknown never matches a value operator.

        bool numeric = target.Field is VideoRuleField.Width or VideoRuleField.Height or VideoRuleField.BitrateKbps
            or VideoRuleField.FileSizeBytes or VideoRuleField.DurationSeconds or VideoRuleField.BitDepth;
        bool boolean = target.Field is VideoRuleField.IsHdr or VideoRuleField.Is4K;

        if (boolean) return BooleanImplies(onField, target);
        if (numeric) return NumericImplies(onField, target);
        return TextImplies(onField, target);
    }

    // ------------------------------------------------------------------ text

    private static string NormalizeText(VideoRuleField field, string value) =>
        field == VideoRuleField.Codec ? VideoSourceFacts.NormalizeCodec(value) : value.Trim().ToLowerInvariant();

    private static bool TextImplies(List<VideoRuleCondition> conjunction, VideoRuleCondition target)
    {
        HashSet<string>? allowed = null;
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var condition in conjunction)
        {
            var values = (condition.Values ?? []).Select(v => NormalizeText(condition.Field, v)).ToList();
            switch (condition.Operator)
            {
                case VideoRuleOperator.Is when values.Count > 0:
                    allowed = Intersect(allowed, [values[0]]);
                    break;
                case VideoRuleOperator.In when values.Count > 0:
                    allowed = Intersect(allowed, values);
                    break;
                case VideoRuleOperator.IsNot when values.Count > 0:
                    excluded.Add(values[0]);
                    break;
                case VideoRuleOperator.NotIn:
                    excluded.UnionWith(values);
                    break;
            }
        }
        allowed?.ExceptWith(excluded);
        if (allowed is { Count: 0 }) return false; // Contradictory rule: report nothing.

        var targetValues = (target.Values ?? []).Select(v => NormalizeText(target.Field, v)).ToList();
        return target.Operator switch
        {
            VideoRuleOperator.Is when targetValues.Count > 0
                => allowed is { Count: 1 } && allowed.Contains(targetValues[0]),
            VideoRuleOperator.In when targetValues.Count > 0
                => allowed is { Count: > 0 } && allowed.All(targetValues.Contains),
            VideoRuleOperator.IsNot when targetValues.Count > 0
                => excluded.Contains(targetValues[0]) || (allowed is { Count: > 0 } && !allowed.Contains(targetValues[0])),
            VideoRuleOperator.NotIn
                => targetValues.Count > 0 && targetValues.All(t =>
                    excluded.Contains(t) || (allowed is { Count: > 0 } && !allowed.Contains(t))),
            _ => false,
        };
    }

    private static HashSet<string> Intersect(HashSet<string>? current, IEnumerable<string> values)
    {
        var incoming = new HashSet<string>(values, StringComparer.Ordinal);
        if (current == null) return incoming;
        current.IntersectWith(incoming);
        return current;
    }

    // --------------------------------------------------------------- boolean

    private static bool BooleanImplies(List<VideoRuleCondition> conjunction, VideoRuleCondition target)
    {
        var allowed = new HashSet<bool> { true, false };
        foreach (var condition in conjunction)
        {
            if (condition.Values is not { Count: > 0 } || !bool.TryParse(condition.Values[0], out var value)) continue;
            if (condition.Operator == VideoRuleOperator.Is) allowed.RemoveWhere(b => b != value);
            else if (condition.Operator == VideoRuleOperator.IsNot) allowed.Remove(value);
        }
        if (allowed.Count != 1) return false;

        if (target.Values is not { Count: > 0 } || !bool.TryParse(target.Values[0], out var expected)) return false;
        var only = allowed.Single();
        return target.Operator switch
        {
            VideoRuleOperator.Is    => only == expected,
            VideoRuleOperator.IsNot => only != expected,
            _ => false,
        };
    }

    // --------------------------------------------------------------- numeric

    private sealed class NumericRange
    {
        public double Lo = double.NegativeInfinity;
        public double Hi = double.PositiveInfinity;
        public bool LoInclusive = true;
        public bool HiInclusive = true;
        public HashSet<double>? Points;
        public readonly HashSet<double> Excluded = new();

        public bool IsEmpty()
        {
            if (Points != null) return Points.Count == 0;
            if (Lo > Hi) return true;
            return Lo == Hi && (!LoInclusive || !HiInclusive);
        }
    }

    private static double? ParseNumber(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool NumericImplies(List<VideoRuleCondition> conjunction, VideoRuleCondition target)
    {
        var range = new NumericRange();
        foreach (var condition in conjunction)
        {
            var parsed = (condition.Values ?? []).Select(ParseNumber).ToList();
            var first = parsed.Count > 0 ? parsed[0] : null;
            switch (condition.Operator)
            {
                // A cond the evaluator can't parse matches nothing; dropping it here
                // only widens the rule, which keeps the analysis conservative.
                case VideoRuleOperator.Is when first.HasValue:
                    range.Points = IntersectPoints(range.Points, [first.Value]);
                    break;
                case VideoRuleOperator.In:
                    var set = parsed.Where(v => v.HasValue).Select(v => v!.Value).ToList();
                    if (set.Count > 0) range.Points = IntersectPoints(range.Points, set);
                    break;
                case VideoRuleOperator.IsNot when first.HasValue:
                    range.Excluded.Add(first.Value);
                    break;
                case VideoRuleOperator.NotIn:
                    foreach (var v in parsed.Where(v => v.HasValue)) range.Excluded.Add(v!.Value);
                    break;
                case VideoRuleOperator.GreaterThan when first.HasValue:
                    Tighten(range, lo: first.Value, loInclusive: false);
                    break;
                case VideoRuleOperator.GreaterThanOrEqual when first.HasValue:
                    Tighten(range, lo: first.Value, loInclusive: true);
                    break;
                case VideoRuleOperator.LessThan when first.HasValue:
                    Tighten(range, hi: first.Value, hiInclusive: false);
                    break;
                case VideoRuleOperator.LessThanOrEqual when first.HasValue:
                    Tighten(range, hi: first.Value, hiInclusive: true);
                    break;
                case VideoRuleOperator.Between when parsed.Count > 1 && first.HasValue && parsed[1].HasValue:
                    Tighten(range, lo: Math.Min(first.Value, parsed[1]!.Value), loInclusive: true);
                    Tighten(range, hi: Math.Max(first.Value, parsed[1]!.Value), hiInclusive: true);
                    break;
            }
        }
        range.Points?.RemoveWhere(p => !InRange(range, p) || range.Excluded.Contains(p));
        if (range.IsEmpty()) return false; // Contradictory rule: report nothing.

        var targetParsed = (target.Values ?? []).Select(ParseNumber).ToList();
        var a = targetParsed.Count > 0 ? targetParsed[0] : null;
        var b = targetParsed.Count > 1 ? targetParsed[1] : null;

        return target.Operator switch
        {
            VideoRuleOperator.Is when a.HasValue => range.Points is { Count: 1 } && range.Points.Contains(a.Value)
                || (range.Points == null && range.Lo == range.Hi && range.LoInclusive && range.HiInclusive && range.Lo == a.Value && !range.Excluded.Contains(a.Value)),
            VideoRuleOperator.In => targetParsed.Any(v => v.HasValue)
                && range.Points is { Count: > 0 }
                && range.Points.All(p => targetParsed.Any(v => v.HasValue && v.Value == p)),
            VideoRuleOperator.IsNot when a.HasValue => NeverEquals(range, a.Value),
            VideoRuleOperator.NotIn => targetParsed.Any(v => v.HasValue)
                && targetParsed.Where(v => v.HasValue).All(v => NeverEquals(range, v!.Value)),
            VideoRuleOperator.GreaterThan when a.HasValue =>
                range.Points?.All(p => p > a.Value)
                ?? (range.Lo > a.Value || (range.Lo == a.Value && !range.LoInclusive)),
            VideoRuleOperator.GreaterThanOrEqual when a.HasValue =>
                range.Points?.All(p => p >= a.Value) ?? range.Lo >= a.Value,
            VideoRuleOperator.LessThan when a.HasValue =>
                range.Points?.All(p => p < a.Value)
                ?? (range.Hi < a.Value || (range.Hi == a.Value && !range.HiInclusive)),
            VideoRuleOperator.LessThanOrEqual when a.HasValue =>
                range.Points?.All(p => p <= a.Value) ?? range.Hi <= a.Value,
            VideoRuleOperator.Between when a.HasValue && b.HasValue =>
                BetweenImplied(range, Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value)),
            _ => false,
        };
    }

    private static void Tighten(NumericRange range, double? lo = null, bool loInclusive = true, double? hi = null, bool hiInclusive = true)
    {
        if (lo.HasValue && (lo.Value > range.Lo || (lo.Value == range.Lo && range.LoInclusive && !loInclusive)))
        {
            range.Lo = lo.Value;
            range.LoInclusive = loInclusive;
        }
        if (hi.HasValue && (hi.Value < range.Hi || (hi.Value == range.Hi && range.HiInclusive && !hiInclusive)))
        {
            range.Hi = hi.Value;
            range.HiInclusive = hiInclusive;
        }
    }

    private static HashSet<double> IntersectPoints(HashSet<double>? current, IEnumerable<double> values)
    {
        var incoming = new HashSet<double>(values);
        if (current == null) return incoming;
        current.IntersectWith(incoming);
        return current;
    }

    private static bool InRange(NumericRange range, double value)
    {
        if (value < range.Lo || (value == range.Lo && !range.LoInclusive)) return false;
        if (value > range.Hi || (value == range.Hi && !range.HiInclusive)) return false;
        return true;
    }

    private static bool NeverEquals(NumericRange range, double value) =>
        range.Points?.All(p => p != value)
        ?? (range.Excluded.Contains(value) || !InRange(range, value));

    private static bool BetweenImplied(NumericRange range, double lo, double hi)
    {
        if (range.Points is { Count: > 0 }) return range.Points.All(p => p >= lo && p <= hi);
        return range.Lo >= lo && range.Hi <= hi;
    }
}
