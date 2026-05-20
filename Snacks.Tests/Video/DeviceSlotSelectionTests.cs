using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for the local-master device selection predicates extracted from
///     <c>TranscodingService.TryReserveLocalDeviceSlot</c>:
///     <see cref="TranscodingService.IsDeviceEligibleUnderHwPref"/> (the hw-vs-CPU gating ladder)
///     and <see cref="TranscodingService.CanDeviceEncodeCodec"/> (codec-vs-device match).
///     The two compose to determine whether a detected device is a valid landing spot for a
///     work item under the current hardware-acceleration preference.
///
///     <para>The regression these guard against: AV1 + Auto on a machine with hardware encoders
///     that lack AV1 support (e.g. Intel UHD 630) used to lock out the CPU fallback because the
///     "has hardware" check was codec-blind. Jobs queued forever instead of falling back to
///     software libsvtav1. The predicate pair must now leave CPU eligible in that scenario while
///     preserving every other branch (specific-vendor still queues forever on impossible codec,
///     none/auto/specific behaviour for working codecs is unchanged).</para>
/// </summary>
public sealed class DeviceSlotSelectionTests
{
    private static HardwareDevice IntelNoAv1() => new()
    {
        DeviceId = "intel",
        SupportedCodecs = new List<string> { "h264", "h265" },
    };

    private static HardwareDevice NvidiaFull() => new()
    {
        DeviceId = "nvidia",
        SupportedCodecs = new List<string> { "h264", "h265", "av1" },
    };

    private static HardwareDevice Cpu() => new()
    {
        DeviceId = "cpu",
        SupportedCodecs = new List<string> { "h264", "h265", "av1" },
    };

    /// <summary>
    ///     Mirrors what <c>TryReserveLocalDeviceSlot</c> does once the ledger
    ///     decisions are stripped out: pick the first device that passes the
    ///     eligibility ladder and the codec match. Returns null when nothing fits.
    /// </summary>
    private static string? PickDevice(IReadOnlyList<HardwareDevice> devices, string hwPref, string codec)
    {
        bool hasHardwareThatCanEncode = devices.Any(d =>
            d.DeviceId != "cpu"
            && TranscodingService.CanDeviceEncodeCodec(d, d.DeviceId, codec));

        foreach (var device in devices)
        {
            if (!TranscodingService.IsDeviceEligibleUnderHwPref(device.DeviceId, hwPref, hasHardwareThatCanEncode))
                continue;
            if (!TranscodingService.CanDeviceEncodeCodec(device, device.DeviceId, codec))
                continue;
            return device.DeviceId;
        }
        return null;
    }


    // ------------------------------------------------------------------ //
    //  IsDeviceEligibleUnderHwPref — the gating ladder
    // ------------------------------------------------------------------ //

    /// <summary>
    ///     Rows: (deviceId, hwPref, hasHardwareThatCanEncode, expectedEligible).
    ///     Encodes every branch of the four-rule ladder. The
    ///     <c>("cpu", "auto", false, true)</c> row is the regression case the fix
    ///     introduces — CPU must be eligible under Auto when no HW device can do
    ///     the requested codec.
    /// </summary>
    public static IEnumerable<object[]> EligibilityRows() => new[]
    {
        // none → CPU only
        new object[] { "cpu",    "none",   true,  true  },
        new object[] { "cpu",    "none",   false, true  },
        new object[] { "intel",  "none",   true,  false },
        new object[] { "nvidia", "none",   false, false },

        // specific vendor → CPU excluded, must match family
        new object[] { "cpu",    "intel",  true,  false },
        new object[] { "cpu",    "intel",  false, false },
        new object[] { "intel",  "intel",  true,  true  },
        new object[] { "intel",  "nvidia", true,  false },
        new object[] { "nvidia", "nvidia", false, true  },

        // auto → HW always eligible, CPU eligible only when no HW can do the codec
        new object[] { "intel",  "auto",   true,  true  },
        new object[] { "intel",  "auto",   false, true  },   // codec check rejects this later
        new object[] { "nvidia", "auto",   true,  true  },
        new object[] { "cpu",    "auto",   true,  false },   // HW can do it ⇒ CPU excluded
        new object[] { "cpu",    "auto",   false, true  },   // regression: HW can't do it ⇒ CPU is fallback
    };

    [Theory]
    [MemberData(nameof(EligibilityRows))]
    public void IsDeviceEligibleUnderHwPref_encodes_gating_ladder(
        string deviceId, string hwPref, bool hasHardwareThatCanEncode, bool expected)
    {
        TranscodingService.IsDeviceEligibleUnderHwPref(deviceId, hwPref, hasHardwareThatCanEncode)
            .Should().Be(expected);
    }


    // ------------------------------------------------------------------ //
    //  CanDeviceEncodeCodec — codec aliasing + null-device handling
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("h264", true)]
    [InlineData("avc",  true)]   // alias
    [InlineData("h265", true)]
    [InlineData("hevc", true)]   // alias
    [InlineData("av1",  false)]  // no AV1 in SupportedCodecs
    [InlineData("vp9",  false)]  // unknown codec, not in supported list
    public void CanDeviceEncodeCodec_intel_no_av1_matches_codec_list(string codec, bool expected)
    {
        TranscodingService.CanDeviceEncodeCodec(IntelNoAv1(), "intel", codec).Should().Be(expected);
    }

    [Theory]
    [InlineData("av1",  true)]
    [InlineData("h265", true)]
    [InlineData("hevc", true)]
    [InlineData("h264", true)]
    [InlineData("avc",  true)]
    public void CanDeviceEncodeCodec_full_nvidia_matches_all_modern_codecs(string codec, bool expected)
    {
        TranscodingService.CanDeviceEncodeCodec(NvidiaFull(), "nvidia", codec).Should().Be(expected);
    }

    [Fact]
    public void CanDeviceEncodeCodec_null_device_treats_cpu_as_capable()
    {
        TranscodingService.CanDeviceEncodeCodec(null, "cpu", "av1").Should().BeTrue();
        TranscodingService.CanDeviceEncodeCodec(null, "cpu", "h264").Should().BeTrue();
    }

    [Fact]
    public void CanDeviceEncodeCodec_null_device_rejects_non_cpu()
    {
        TranscodingService.CanDeviceEncodeCodec(null, "nvidia", "av1").Should().BeFalse();
        TranscodingService.CanDeviceEncodeCodec(null, "intel",  "h264").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void CanDeviceEncodeCodec_unknown_codec_falls_through_to_ffmpeg(string? codec)
    {
        // Empty / unknown codec returns true so the caller hands it to ffmpeg unchanged
        // rather than synthesising a refusal — matches the pre-existing semantics of
        // the private predicate this method was extracted from.
        TranscodingService.CanDeviceEncodeCodec(IntelNoAv1(), "intel", codec).Should().BeTrue();
    }


    // ------------------------------------------------------------------ //
    //  Composed dispatcher behaviour
    // ------------------------------------------------------------------ //

    /// <summary>
    ///     The regression test for the Intel iGPU without AV1 hardware encode
    ///     plus an AV1 job under Auto must dispatch to CPU rather than returning null and
    ///     spinning the queue.
    /// </summary>
    [Fact]
    public void Auto_with_av1_and_intel_only_hw_falls_back_to_cpu()
    {
        var devices = new List<HardwareDevice> { IntelNoAv1(), Cpu() };
        PickDevice(devices, "auto", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     Regression guard: the codec-aware fix must not change behaviour for codecs the
    ///     hardware does support. H.264 + Auto on the same Intel-only system still picks
    ///     Intel; the new branch only kicks in when no HW device can do the codec.
    /// </summary>
    [Fact]
    public void Auto_with_h264_and_intel_only_hw_still_picks_intel()
    {
        var devices = new List<HardwareDevice> { IntelNoAv1(), Cpu() };
        PickDevice(devices, "auto", "h264").Should().Be("intel");
    }

    /// <summary>
    ///     Auto still prefers hardware over CPU when at least one HW device can do the codec —
    ///     even if another HW device can't. Intel can't do AV1, NVIDIA can; NVIDIA wins,
    ///     CPU stays excluded.
    /// </summary>
    [Fact]
    public void Auto_with_mixed_hw_prefers_capable_hardware_over_cpu()
    {
        var devices = new List<HardwareDevice> { IntelNoAv1(), NvidiaFull(), Cpu() };
        PickDevice(devices, "auto", "av1").Should().Be("nvidia");
    }

    /// <summary>
    ///     Software mode picks CPU regardless of which HW devices exist or what they support.
    /// </summary>
    [Fact]
    public void None_with_av1_picks_cpu_regardless_of_hw()
    {
        var devices = new List<HardwareDevice> { NvidiaFull(), IntelNoAv1(), Cpu() };
        PickDevice(devices, "none", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     Explicit-vendor + impossible-codec is the "queue forever" case the fix
    ///     deliberately leaves unchanged. The user picked Intel specifically — falling back
    ///     to CPU silently would be doing something other than what they asked for. Better
    ///     to surface the misconfiguration by leaving the job pending.
    /// </summary>
    [Fact]
    public void Specific_intel_with_av1_returns_null_when_intel_lacks_av1()
    {
        var devices = new List<HardwareDevice> { IntelNoAv1(), Cpu() };
        PickDevice(devices, "intel", "av1").Should().BeNull();
    }

    /// <summary>
    ///     Auto + AV1 on a machine with no detected hardware at all routes to CPU directly.
    /// </summary>
    [Fact]
    public void Auto_with_av1_and_cpu_only_picks_cpu()
    {
        var devices = new List<HardwareDevice> { Cpu() };
        PickDevice(devices, "auto", "av1").Should().Be("cpu");
    }
}
