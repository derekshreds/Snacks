using FluentAssertions;
using Snacks.Models;
using Snacks.Services.Routing;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Tests for <see cref="VideoJobRouter.ScoreSlot"/>'s hardware-acceleration gating —
///     the cluster-path mirror of <c>TranscodingService.IsDeviceEligibleUnderHwPref</c>.
///
///     <para>The regression these guard against: an explicit vendor preference (e.g. "amd")
///     for a codec that vendor can't encode (AV1 on a pre-RDNA3 Radeon, whose device advertises
///     only h264/h265) used to score every slot ≤ 0 — the GPU slot failed the codec match and
///     CPU was excluded outright — so the job never placed anywhere. CPU must now score positive
///     as a software fallback in exactly that case, while staying excluded when the chosen vendor
///     <em>can</em> do the codec (so a capable-but-busy GPU isn't downgraded to software).</para>
/// </summary>
public sealed class VideoJobRouterScoreTests
{
    // ScoreSlot never dereferences the injected TranscodingService (it's only used by
    // EncodeRemoteAsync / ExpectedOutputExtension), so null is safe for scoring tests.
    private static readonly VideoJobRouter Router = new(null!);

    private static ScoreContext Ctx() => new()
    {
        IsDeviceEnabled         = (_, _) => true,
        UsedDeviceSlots         = (_, _) => 0,
        EffectiveDeviceCapacity = (_, _) => 1,
        GetNodeSettings         = _ => null,
    };

    private static HardwareDevice AmdNoAv1() => new()
    {
        DeviceId = "amd",
        DisplayName = "AMD VAAPI",
        SupportedCodecs = new List<string> { "h264", "h265" },
        DefaultConcurrency = 1,
        IsHardware = true,
    };

    private static HardwareDevice Cpu() => new()
    {
        DeviceId = "cpu",
        SupportedCodecs = new List<string> { "h264", "h265", "av1" },
        DefaultConcurrency = 1,
        IsHardware = true,
    };

    private static ClusterNode NodeWith(params HardwareDevice[] devices) => new()
    {
        NodeId = "n1",
        Capabilities = new WorkerCapabilities
        {
            AvailableDiskSpaceBytes = long.MaxValue,
            Devices = devices.ToList(),
        },
    };

    private static int Score(ClusterNode node, HardwareDevice device, string hw, string codec)
    {
        var item = new WorkItem { Size = 0, Is4K = false };
        var options = new EncoderOptions { Codec = codec, HardwareAcceleration = hw };
        return Router.ScoreSlot(node, device, item, options, Ctx());
    }

    /// <summary>
    ///     Reporter's case: AMD selected explicitly, AV1 output, AMD can't do AV1. The AMD slot
    ///     is rejected (codec mismatch) and CPU becomes the positive-scoring software fallback.
    /// </summary>
    [Fact]
    public void Amd_av1_when_amd_lacks_av1_scores_cpu_fallback_positive()
    {
        var amd = AmdNoAv1();
        var cpu = Cpu();
        var node = NodeWith(amd, cpu);

        Score(node, amd, "amd", "av1").Should().BeLessThanOrEqualTo(0);
        Score(node, cpu, "amd", "av1").Should().BeGreaterThan(0);
    }

    /// <summary>
    ///     AMD can do HEVC, so an explicit AMD pick lands on the GPU and CPU stays excluded —
    ///     a capable vendor must not be downgraded to software just because it might be busy.
    /// </summary>
    [Fact]
    public void Amd_hevc_when_amd_capable_scores_gpu_and_excludes_cpu()
    {
        var amd = AmdNoAv1();
        var cpu = Cpu();
        var node = NodeWith(amd, cpu);

        Score(node, amd, "amd", "hevc").Should().BeGreaterThan(0);
        Score(node, cpu, "amd", "hevc").Should().BeLessThanOrEqualTo(0);
    }

    /// <summary>
    ///     Explicit AMD on a node with no AMD device at all (only CPU) falls back to software
    ///     rather than scoring nothing.
    /// </summary>
    [Fact]
    public void Amd_av1_with_no_amd_device_scores_cpu_fallback_positive()
    {
        var cpu = Cpu();
        var node = NodeWith(cpu);

        Score(node, cpu, "amd", "av1").Should().BeGreaterThan(0);
    }
}
