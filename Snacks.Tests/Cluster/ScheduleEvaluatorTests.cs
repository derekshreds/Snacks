using System;
using System.Collections.Generic;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the schedule-window evaluation used to gate per-node transcode
///     dispatch. The evaluator is a pure function of (windows, now); these
///     tests drive it with synthetic <see cref="DateTime"/> values rather
///     than relying on the wall clock.
/// </summary>
public sealed class ScheduleEvaluatorTests
{
    private static ScheduleWindow Window(string start, string end, params DayOfWeek[] days) => new()
    {
        Days  = new List<DayOfWeek>(days),
        Start = start,
        End   = end,
    };

    // =====================================================================
    //  Empty / null schedule = always allowed (preserves pre-feature behavior)
    // =====================================================================

    [Fact]
    public void NullWindows_AlwaysAllowed()
    {
        ScheduleEvaluator.IsWithinAny(null, DateTime.Now).Should().BeTrue();
    }

    [Fact]
    public void EmptyWindows_AlwaysAllowed()
    {
        ScheduleEvaluator.IsWithinAny(new List<ScheduleWindow>(), DateTime.Now).Should().BeTrue();
    }

    // =====================================================================
    //  Same-day windows
    // =====================================================================

    [Fact]
    public void SameDayWindow_InsideRange_Matches()
    {
        var w = Window("09:00", "17:00", DayOfWeek.Wednesday);
        var now = new DateTime(2026, 5, 6, 12, 30, 0);   // Wed 12:30
        ScheduleEvaluator.IsWithin(w, now).Should().BeTrue();
    }

    [Fact]
    public void SameDayWindow_AtStartBoundary_Matches()
    {
        var w = Window("09:00", "17:00", DayOfWeek.Wednesday);
        var now = new DateTime(2026, 5, 6, 9, 0, 0);
        ScheduleEvaluator.IsWithin(w, now).Should().BeTrue();
    }

    [Fact]
    public void SameDayWindow_AtEndBoundary_DoesNotMatch()
    {
        // [Start, End) — End is exclusive so a window can butt up against another
        // without a one-minute overlap.
        var w = Window("09:00", "17:00", DayOfWeek.Wednesday);
        var now = new DateTime(2026, 5, 6, 17, 0, 0);
        ScheduleEvaluator.IsWithin(w, now).Should().BeFalse();
    }

    [Fact]
    public void SameDayWindow_WrongDay_DoesNotMatch()
    {
        var w = Window("09:00", "17:00", DayOfWeek.Wednesday);
        var now = new DateTime(2026, 5, 7, 12, 30, 0);   // Thu 12:30
        ScheduleEvaluator.IsWithin(w, now).Should().BeFalse();
    }

    // =====================================================================
    //  Cross-midnight windows (End ≤ Start wraps to next day)
    // =====================================================================

    [Fact]
    public void CrossMidnight_FirstHalfOnListedDay_Matches()
    {
        // Mon-Fri 22:00–06:00 — late Monday should match.
        var w = Window("22:00", "06:00",
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);
        var now = new DateTime(2026, 5, 4, 23, 30, 0);   // Mon 23:30
        ScheduleEvaluator.IsWithin(w, now).Should().BeTrue();
    }

    [Fact]
    public void CrossMidnight_WrapHalfOnDayAfterListedDay_Matches()
    {
        // Mon-Fri 22:00–06:00 — Tue 03:00 is the wrap of Mon's window. Tue is also
        // listed, but the gate that lets this match here is the "yesterday-listed"
        // check on the wrap half.
        var w = Window("22:00", "06:00",
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);
        var now = new DateTime(2026, 5, 5, 3, 0, 0);   // Tue 03:00
        ScheduleEvaluator.IsWithin(w, now).Should().BeTrue();
    }

    [Fact]
    public void CrossMidnight_WrapOnlyMatchesIfPrecedingDayListed()
    {
        // Days = [Monday] only, 22:00–06:00. Tue 03:00 is the wrap of Mon — should match.
        // Wed 03:00 is the wrap of Tue, which is NOT listed — should NOT match.
        var w = Window("22:00", "06:00", DayOfWeek.Monday);

        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 5, 3, 0, 0)).Should().BeTrue();   // Tue 03:00
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 6, 3, 0, 0)).Should().BeFalse();  // Wed 03:00
    }

    [Fact]
    public void CrossMidnight_BetweenEndAndStartOnListedDay_DoesNotMatch()
    {
        // 22:00–06:00 means "outside business hours". Mid-afternoon should never match.
        var w = Window("22:00", "06:00", DayOfWeek.Monday);
        var now = new DateTime(2026, 5, 4, 14, 0, 0);   // Mon 14:00
        ScheduleEvaluator.IsWithin(w, now).Should().BeFalse();
    }

    // =====================================================================
    //  Full-24h windows (Start == End)
    // =====================================================================

    [Fact]
    public void FullDayWindow_AtMidnight_Matches()
    {
        var w = Window("00:00", "00:00", DayOfWeek.Saturday, DayOfWeek.Sunday);
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 9, 0, 0, 0)).Should().BeTrue();    // Sat 00:00
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 9, 23, 59, 0)).Should().BeTrue();  // Sat 23:59
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 10, 12, 0, 0)).Should().BeTrue();  // Sun 12:00
    }

    [Fact]
    public void FullDayWindow_OnUnlistedDay_DoesNotMatch()
    {
        var w = Window("00:00", "00:00", DayOfWeek.Saturday, DayOfWeek.Sunday);
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 6, 12, 0, 0)).Should().BeFalse();  // Wed 12:00
    }

    [Fact]
    public void StartEqualsEnd_NonZero_StillFullDay()
    {
        // Documented behavior: any Start == End (including non-midnight) is treated as
        // full 24h. This keeps the evaluator's branching simple and matches what users
        // typing "12:00 to 12:00" likely mean ("all day").
        var w = Window("12:00", "12:00", DayOfWeek.Tuesday);
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 5, 3, 0, 0)).Should().BeTrue();
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 5, 23, 0, 0)).Should().BeTrue();
        ScheduleEvaluator.IsWithin(w, new DateTime(2026, 5, 6, 12, 0, 0)).Should().BeFalse();
    }

    // =====================================================================
    //  Empty days / malformed times = no match (defensive)
    // =====================================================================

    [Fact]
    public void EmptyDays_NeverMatches()
    {
        var w = Window("00:00", "23:59");   // no days
        ScheduleEvaluator.IsWithin(w, DateTime.Now).Should().BeFalse();
    }

    [Theory]
    [InlineData("",       "17:00")]
    [InlineData("24:00",  "17:00")]
    [InlineData("09:60",  "17:00")]
    [InlineData("nope",   "17:00")]
    [InlineData("09:00",  "")]
    [InlineData("09:00",  "abcd")]
    public void MalformedTimes_DoNotMatch(string start, string end)
    {
        var w = Window(start, end, DayOfWeek.Wednesday);
        var now = new DateTime(2026, 5, 6, 12, 0, 0);
        ScheduleEvaluator.IsWithin(w, now).Should().BeFalse();
    }

    // =====================================================================
    //  IsWithinAny: any-of semantics
    // =====================================================================

    [Fact]
    public void IsWithinAny_AnyMatchingWindowReturnsTrue()
    {
        // A weekday-night window plus a weekend-all-day window — covers the common
        // "always available off-hours" pattern.
        var weekday = Window("22:00", "06:00",
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);
        var weekend = Window("00:00", "00:00", DayOfWeek.Saturday, DayOfWeek.Sunday);
        var schedule = new[] { weekday, weekend };

        ScheduleEvaluator.IsWithinAny(schedule, new DateTime(2026, 5, 4, 23, 0, 0)).Should().BeTrue();   // Mon 23:00 → weekday
        ScheduleEvaluator.IsWithinAny(schedule, new DateTime(2026, 5, 9, 14, 0, 0)).Should().BeTrue();   // Sat 14:00 → weekend
        ScheduleEvaluator.IsWithinAny(schedule, new DateTime(2026, 5, 6, 14, 0, 0)).Should().BeFalse();  // Wed 14:00 → neither
    }
}
