using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Pure schedule evaluation — given a list of <see cref="ScheduleWindow"/>
///     entries and a wall-clock time, decide whether "now" falls inside any
///     of them. Lives in its own static class so the dispatcher can ask a
///     yes/no question without pulling in any persistence or DI plumbing,
///     and so unit tests can drive it with arbitrary <see cref="DateTime"/>
///     inputs.
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>
    ///     True if <paramref name="localNow"/> sits inside any of the
    ///     supplied windows. A null or empty collection returns
    ///     <see langword="true"/> — "no schedule configured" is the
    ///     pre-feature behavior of "always allowed".
    /// </summary>
    public static bool IsWithinAny(IEnumerable<ScheduleWindow>? windows, DateTime localNow)
    {
        if (windows == null) return true;

        bool any = false;
        foreach (var w in windows)
        {
            any = true;
            if (IsWithin(w, localNow)) return true;
        }
        return !any;
    }

    /// <summary>
    ///     True if <paramref name="localNow"/> sits inside <paramref name="window"/>.
    ///
    ///     Rules:
    ///       • An empty <see cref="ScheduleWindow.Days"/> list never matches.
    ///       • Malformed Start/End strings never match (defensive — a bad config
    ///         row shouldn't silently open or close the gate).
    ///       • Start == End is interpreted as "full 24 hours on each listed day".
    ///       • Start &lt; End is a same-day window: match iff today is listed
    ///         and the current time lies in [Start, End).
    ///       • Start &gt; End wraps past midnight: the window opens on the
    ///         listed day at Start and closes on the *next* day at End.
    /// </summary>
    public static bool IsWithin(ScheduleWindow window, DateTime localNow)
    {
        if (window.Days == null || window.Days.Count == 0) return false;

        var startMin = ParseHHmm(window.Start);
        var endMin   = ParseHHmm(window.End);
        if (startMin is null || endMin is null) return false;

        int nowMin = localNow.Hour * 60 + localNow.Minute;
        var today  = localNow.DayOfWeek;

        if (startMin == endMin)
        {
            return window.Days.Contains(today);
        }

        if (startMin < endMin)
        {
            return window.Days.Contains(today)
                && nowMin >= startMin
                && nowMin <  endMin;
        }

        var yesterday = localNow.AddDays(-1).DayOfWeek;
        bool inFirstHalf = window.Days.Contains(today)     && nowMin >= startMin;
        bool inWrapHalf  = window.Days.Contains(yesterday) && nowMin <  endMin;
        return inFirstHalf || inWrapHalf;
    }

    /// <summary>
    ///     Parses an "HH:mm" 24-hour clock string into minutes since midnight.
    ///     Returns <see langword="null"/> for any input that doesn't match
    ///     0–23 hours and 0–59 minutes; callers treat null as "no match".
    /// </summary>
    private static int? ParseHHmm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out int h) || h < 0 || h > 23) return null;
        if (!int.TryParse(parts[1], out int m) || m < 0 || m > 59) return null;
        return h * 60 + m;
    }
}
