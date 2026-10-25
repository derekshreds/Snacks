using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>
///     Only failed sign-ins count toward the throttle, so frequent logins with the right
///     password are never blocked; each client's budget is independent and expires.
/// </summary>
public sealed class LoginThrottleTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_client_is_blocked_only_after_the_failure_budget_is_spent()
    {
        var throttle = new LoginThrottle(new ManualClock());

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.RecordFailure("203.0.113.9");
        throttle.RetryAfterSeconds("203.0.113.9").Should().Be(0);

        throttle.RecordFailure("203.0.113.9");
        throttle.RetryAfterSeconds("203.0.113.9").Should().BeInRange(1, (int)LoginThrottle.Window.TotalSeconds);
        throttle.RetryAfterSeconds("198.51.100.4").Should().Be(0);
    }

    [Fact]
    public void The_block_lifts_when_the_window_expires()
    {
        var clock    = new ManualClock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) throttle.RecordFailure("203.0.113.9");

        clock.Now += LoginThrottle.Window;

        throttle.RetryAfterSeconds("203.0.113.9").Should().Be(0);
        throttle.RecordFailure("203.0.113.9");
        throttle.RetryAfterSeconds("203.0.113.9").Should().Be(0);
    }
}
