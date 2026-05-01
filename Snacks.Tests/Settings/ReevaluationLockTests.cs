using FluentAssertions;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Pins the <see cref="SemaphoreSlim"/> pattern the re-evaluate endpoint uses to
///     prevent concurrent runs. The endpoint itself depends on a live <c>TranscodingService</c>
///     and <c>MediaFileRepository</c>, so this suite exercises the lock semantics directly
///     rather than spinning up the full HTTP stack — the controller code is a thin wrapper
///     around exactly this pattern.
/// </summary>
public sealed class ReevaluationLockTests
{
    [Fact]
    public async Task Single_holder_blocks_a_concurrent_zero_timeout_acquire()
    {
        using var gate = new SemaphoreSlim(1, 1);

        (await gate.WaitAsync(0)).Should().BeTrue("the gate is unheld at start");
        (await gate.WaitAsync(0)).Should().BeFalse("a second acquire while held must reject");

        gate.Release();
        (await gate.WaitAsync(0)).Should().BeTrue("post-release the gate is acquirable again");
        gate.Release();
    }


    [Fact]
    public async Task Many_concurrent_acquires_serialize_to_exactly_one_winner()
    {
        // Models the "user mashes the button" case: N requests fired simultaneously,
        // exactly one acquires the lock, the rest fail-fast at 0 timeout.
        using var gate = new SemaphoreSlim(1, 1);

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => gate.WaitAsync(0)));

        attempts.Count(succeeded => succeeded).Should().Be(1);
        gate.Release();
    }


    [Fact]
    public async Task Release_in_finally_recovers_the_gate_after_exception()
    {
        // Simulates the controller's try/finally pattern. An exception during the
        // re-evaluation must not leave the gate permanently held, otherwise a single
        // bad run wedges the button forever.
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        try   { throw new InvalidOperationException("simulated failure"); }
        catch { /* swallow */ }
        finally { gate.Release(); }

        (await gate.WaitAsync(0)).Should().BeTrue();
        gate.Release();
    }
}
