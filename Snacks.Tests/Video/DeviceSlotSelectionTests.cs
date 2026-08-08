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
///     <para>The regressions these guard against: (1) AV1 + Auto on a machine with hardware
///     encoders that lack AV1 support (e.g. Intel UHD 630) used to lock out the CPU fallback
///     because the "has hardware" check was codec-blind; (2) an explicit vendor pick (e.g. AMD)
///     for a codec that vendor can't encode (AV1 on a pre-RDNA3 Radeon) — or a vendor that
///     isn't detected at all on Linux — used to leave the job stuck Pending forever. Both now
///     fall back to software (CPU) with a caller-emitted warning, via the
///     <c>selectedVendorCanEncode</c> arm of the ladder. A vendor device that <em>can</em> do
///     the codec but is merely busy still keeps CPU excluded (the job queues for the GPU);
///     none/auto/specific behaviour for working codecs is otherwise unchanged.</para>
/// </summary>
public sealed class DeviceSlotSelectionTests
{
    private static HardwareDevice IntelNoAv1() => new()
    {
        DeviceId = "intel",
        SupportedCodecs = new List<string> { "h264", "h265" },
    };

    private static HardwareDevice AmdNoAv1() => new()
    {
        DeviceId = "amd",
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
        bool selectedVendorCanEncode = devices.Any(d =>
            d.DeviceId != "cpu"
            && string.Equals(d.DeviceId, hwPref, StringComparison.OrdinalIgnoreCase)
            && TranscodingService.CanDeviceEncodeCodec(d, d.DeviceId, codec));

        foreach (var device in devices)
        {
            if (!TranscodingService.IsDeviceEligibleUnderHwPref(device.DeviceId, hwPref, hasHardwareThatCanEncode, selectedVendorCanEncode))
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
    ///     Rows: (deviceId, hwPref, hasHardwareThatCanEncode, selectedVendorCanEncode, expectedEligible).
    ///     Encodes every branch of the ladder. The <c>("cpu", "auto", false, …, true)</c> row is the
    ///     original Auto fallback; the <c>("cpu", "intel", …, false, true)</c> row is the new
    ///     specific-vendor fallback — CPU becomes eligible when the chosen vendor can't serve the codec.
    /// </summary>
    public static IEnumerable<object[]> EligibilityRows() => new[]
    {
        // none → CPU only (selectedVendorCanEncode irrelevant)
        new object[] { "cpu",    "none",   true,  false, true  },
        new object[] { "cpu",    "none",   false, false, true  },
        new object[] { "intel",  "none",   true,  false, false },
        new object[] { "nvidia", "none",   false, false, false },

        // specific vendor → matching HW family eligible; CPU eligible ONLY as a software
        // fallback when the chosen vendor can't serve the codec (selectedVendorCanEncode=false)
        new object[] { "cpu",    "intel",  true,  true,  false },  // intel can do it ⇒ no CPU fallback
        new object[] { "cpu",    "intel",  false, false, true  },  // intel can't do it ⇒ CPU is the fallback
        new object[] { "cpu",    "amd",    false, false, true  },  // amd absent/incapable ⇒ CPU is the fallback
        new object[] { "intel",  "intel",  true,  true,  true  },
        new object[] { "intel",  "nvidia", true,  true,  false },  // wrong family
        new object[] { "nvidia", "nvidia", false, true,  true  },

        // auto → HW always eligible, CPU eligible only when no HW can do the codec
        new object[] { "intel",  "auto",   true,  false, true  },
        new object[] { "intel",  "auto",   false, false, true  },  // codec check rejects this later
        new object[] { "nvidia", "auto",   true,  false, true  },
        new object[] { "cpu",    "auto",   true,  false, false },  // HW can do it ⇒ CPU excluded
        new object[] { "cpu",    "auto",   false, false, true  },  // HW can't do it ⇒ CPU is fallback
    };

    [Theory]
    [MemberData(nameof(EligibilityRows))]
    public void IsDeviceEligibleUnderHwPref_encodes_gating_ladder(
        string deviceId, string hwPref, bool hasHardwareThatCanEncode, bool selectedVendorCanEncode, bool expected)
    {
        TranscodingService.IsDeviceEligibleUnderHwPref(deviceId, hwPref, hasHardwareThatCanEncode, selectedVendorCanEncode)
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
    ///     Explicit-vendor + impossible-codec now falls back to software instead of queueing
    ///     forever. Previously this returned null ("surface the misconfiguration by leaving the
    ///     job pending"), but a job stuck Pending with no signal was the actual reported bug —
    ///     the chosen behaviour is now fall-back-to-software with a one-time warning emitted by
    ///     the caller (TryReserveLocalDeviceSlot).
    /// </summary>
    [Fact]
    public void Specific_intel_with_av1_falls_back_to_cpu_when_intel_lacks_av1()
    {
        var devices = new List<HardwareDevice> { IntelNoAv1(), Cpu() };
        PickDevice(devices, "intel", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     The reporter's exact case: AMD (RDNA1) selected explicitly, AV1 output. The AMD VAAPI
    ///     device has no AV1 encoder, so the job falls back to software (CPU) instead of stalling
    ///     in Pending forever.
    /// </summary>
    [Fact]
    public void Specific_amd_with_av1_falls_back_to_cpu_when_amd_lacks_av1()
    {
        var devices = new List<HardwareDevice> { AmdNoAv1(), Cpu() };
        PickDevice(devices, "amd", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     With the AMD device present and capable of the codec (HEVC), an explicit AMD
    ///     preference lands on the GPU — CPU stays excluded so a job the hardware can actually
    ///     do isn't downgraded to software.
    /// </summary>
    [Fact]
    public void Specific_amd_with_hevc_picks_amd_when_capable()
    {
        var devices = new List<HardwareDevice> { AmdNoAv1(), Cpu() };
        PickDevice(devices, "amd", "hevc").Should().Be("amd");
    }

    /// <summary>
    ///     Explicit AMD on a machine where AMD wasn't detected at all (only CPU present) — e.g. a
    ///     host without the render node, or before the Linux vendor-labelling fix. Falls back to
    ///     software rather than stranding the job.
    /// </summary>
    [Fact]
    public void Specific_amd_with_no_amd_device_falls_back_to_cpu()
    {
        var devices = new List<HardwareDevice> { Cpu() };
        PickDevice(devices, "amd", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     Busy-but-capable is NOT a fallback trigger: when the chosen vendor CAN do the codec,
    ///     the ladder keeps CPU excluded (the job queues for the GPU rather than spilling onto a
    ///     slow software encode). Pinned directly on the predicate since PickDevice models no
    ///     capacity.
    /// </summary>
    [Fact]
    public void Specific_amd_capable_keeps_cpu_excluded()
    {
        TranscodingService.IsDeviceEligibleUnderHwPref("cpu", "amd",
            hasHardwareThatCanEncode: true, selectedVendorCanEncode: true).Should().BeFalse();
    }

    /// <summary>
    ///     Vendor isolation: an explicit AMD pick for AV1 falls back to <em>software</em>, never
    ///     to a different vendor's hardware — even when an AV1-capable NVIDIA card is present.
    ///     The user asked for AMD specifically; silently using NVIDIA would be doing something
    ///     other than what they asked, so CPU is the only fallback.
    /// </summary>
    [Fact]
    public void Specific_amd_with_av1_falls_back_to_cpu_not_to_other_vendor_hw()
    {
        var devices = new List<HardwareDevice> { AmdNoAv1(), NvidiaFull(), Cpu() };
        PickDevice(devices, "amd", "av1").Should().Be("cpu");
    }

    /// <summary>
    ///     Explicit NVIDIA on a machine with no NVIDIA device falls back to software too — the
    ///     fix isn't AMD-specific, it covers every vendor that can't service the job.
    /// </summary>
    [Fact]
    public void Specific_nvidia_with_no_nvidia_device_falls_back_to_cpu()
    {
        var devices = new List<HardwareDevice> { AmdNoAv1(), Cpu() };
        PickDevice(devices, "nvidia", "h264").Should().Be("cpu");
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
