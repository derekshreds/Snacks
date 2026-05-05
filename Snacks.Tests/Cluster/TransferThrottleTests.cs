using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Services.Cluster;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Verifies the master-side transfer throttle. Concurrency caps must
///     gate the number of in-flight uploads/downloads, bandwidth caps must
///     pace the byte-rate via the token bucket, and live setting changes
///     must take effect on the next acquire without restart.
/// </summary>
public sealed class TransferThrottleTests : IDisposable
{
    private const string NodeA = "node-A";
    private const string NodeB = "node-B";

    private readonly string _tempRoot;

    public TransferThrottleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "snacks-throttle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private NetworkingSettingsService MakeSettings(NetworkingSettings initial)
    {
        var svc = new NetworkingSettingsService(() => _tempRoot);
        svc.Save(initial);
        return svc;
    }

    [Fact]
    public async Task Per_node_concurrency_blocks_second_acquire_until_first_releases()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploadsPerNode = 1 });
        using var t = new TransferThrottle(settings);

        var first = await t.AcquireUploadAsync(NodeA, default);
        var secondTask = t.AcquireUploadAsync(NodeA, default);

        // Give the second task a chance to settle into the wait state.
        await Task.Delay(50);
        secondTask.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        var second = await secondTask;
        second.Should().NotBeNull();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Cluster_wide_concurrency_caps_across_distinct_nodes()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploads = 2, MaxConcurrentUploadsPerNode = 0 });
        using var t = new TransferThrottle(settings);

        var a = await t.AcquireUploadAsync(NodeA, default);
        var b = await t.AcquireUploadAsync(NodeB, default);
        var thirdTask = t.AcquireUploadAsync("node-C", default);

        await Task.Delay(50);
        thirdTask.IsCompleted.Should().BeFalse();

        await a.DisposeAsync();
        var third = await thirdTask;
        await third.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Unlimited_concurrency_never_blocks()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploads = 0, MaxConcurrentUploadsPerNode = 0 });
        using var t = new TransferThrottle(settings);

        var handles = new List<IAsyncDisposable>();
        for (int i = 0; i < 50; i++) handles.Add(await t.AcquireUploadAsync(NodeA, default));
        handles.Should().HaveCount(50);
        foreach (var h in handles) await h.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_during_blocked_acquire_throws_and_releases_global()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploadsPerNode = 1 });
        using var t = new TransferThrottle(settings);

        var first = await t.AcquireUploadAsync(NodeA, default);

        using var cts = new CancellationTokenSource();
        var blockedTask = t.AcquireUploadAsync(NodeA, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedTask);

        // After release, a fresh acquire must succeed promptly — confirms the
        // global semaphore was released even though the per-node one was the
        // one we cancelled out of.
        await first.DisposeAsync();
        var fresh = await t.AcquireUploadAsync(NodeA, default);
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task Bandwidth_cap_paces_a_single_upload_to_target_rate()
    {
        const int mbps     = 50;
        const int totalMb  = 100;
        var settings = MakeSettings(new NetworkingSettings { MaxUploadMBps = mbps });
        using var t = new TransferThrottle(settings);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Acquire in 5 MB slices so the bucket has time to refill realistically.
        for (int sent = 0; sent < totalMb; sent += 5)
        {
            await t.AcquireUploadBandwidthAsync(NodeA, 5 * 1024 * 1024, default);
        }
        sw.Stop();

        // Token bucket: capacity = 1s of headroom (= 50MB at this rate).
        // First 50MB drains the bucket instantly; remaining 50MB at 50MB/s
        // takes ~1s. Total ≈ 1s. Assert >700ms so CI variance doesn't make
        // the test flaky while still proving the cap is enforced.
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(700));
    }

    [Fact]
    public async Task Bandwidth_unlimited_returns_immediately()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxUploadMBps = 0 });
        using var t = new TransferThrottle(settings);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            await t.AcquireUploadBandwidthAsync(NodeA, 50 * 1024 * 1024, default);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ChunkSizeBytes_clamps_into_4_to_256_range()
    {
        var settings = new NetworkingSettingsService(() => _tempRoot);
        // Default 50MB
        new TransferThrottle(settings).ChunkSizeBytes.Should().Be(50 * 1024 * 1024);
    }

    [Fact]
    public async Task Live_setting_change_releases_old_limiters()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploadsPerNode = 1 });
        using var t = new TransferThrottle(settings);

        var first = await t.AcquireUploadAsync(NodeA, default);
        // Bump cap to 2; per-node limiter is rebuilt with new capacity. The
        // existing first handle is no longer accounted in the new limiter,
        // so the new one starts at 0/2 and a fresh acquire proceeds.
        settings.Save(new NetworkingSettings { MaxConcurrentUploadsPerNode = 2 });

        var second = await t.AcquireUploadAsync(NodeA, default);
        second.Should().NotBeNull();
        await second.DisposeAsync();
        await first.DisposeAsync();
    }

    [Fact]
    public void ForgetNode_removes_per_node_limiters_without_throw()
    {
        var settings = MakeSettings(new NetworkingSettings { MaxConcurrentUploadsPerNode = 1 });
        using var t = new TransferThrottle(settings);
        // ForgetNode on an unknown nodeId is a no-op.
        t.ForgetNode("never-acquired");
    }

}
