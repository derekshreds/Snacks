using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for the encoder probe-attempt builder
///     (<see cref="TranscodingService.BuildEncoderProbeAttempts"/>) behind hardware
///     detection and the pre-encode capability check. The VAAPI attempt matrix is the
///     load-bearing part: AMD (Mesa) only exposes the normal entrypoint and fails
///     <c>-low_power</c>, while LP-only Intel parts (Jasper Lake, Elkhart Lake,
///     ADL-N/Twin Lake N-series) only expose the low-power entrypoint and fail plain
///     CQP. Each side of that matrix has already regressed once — these rows pin both.
/// </summary>
public sealed class EncoderProbeTests
{
    private const string HwFlags = "-init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw";

    /// <summary>
    ///     VAAPI encoders must be probed twice — plain first (AMD, full-featured
    ///     Intel), then with <c>-low_power 1</c> (LP-only Intel N-series). One passing
    ///     attempt marks the encoder usable. Rows: (encoder, expected fixed CQP).
    /// </summary>
    [Theory]
    [InlineData("hevc_vaapi", 25)]
    [InlineData("h264_vaapi", 25)]
    [InlineData("av1_vaapi", 125)]
    public void Vaapi_probe_tries_plain_then_low_power(string encoder, int expectedCqp)
    {
        var attempts = TranscodingService.BuildEncoderProbeAttempts(HwFlags, encoder);

        attempts.Should().HaveCount(2);

        var (plainArgs, plainVariant) = attempts[0];
        var (lpArgs, lpVariant)       = attempts[1];

        // Plain goes first so healthy AMD / full-featured Intel never pay a second probe.
        plainVariant.Should().BeEmpty();
        plainArgs.Should().NotContain("-low_power");

        // The retry is what keeps LP-only Intel (N100/N305/N355, Jasper/Elkhart Lake)
        // from silently falling back to CPU — the regression behind the N355 report.
        lpVariant.Should().Be("low_power");
        lpArgs.Should().Contain("-low_power 1");

        foreach (var (args, _) in attempts)
        {
            args.Should().Contain(HwFlags);
            args.Should().Contain("-vf format=nv12|vaapi,hwupload");
            args.Should().Contain($"-rc_mode CQP -global_quality:v {expectedCqp}");
            args.Should().Contain($"-c:v {encoder}");
            args.Should().Contain("-frames:v 1");
        }
    }

    /// <summary>
    ///     AMF encoders are probed twice: bare defaults, then an explicitly configured
    ///     session. RDNA4 + recent Adrenalin drivers have failed per-codec AMF init
    ///     under default parameters while an explicit usage/quality/QP session encoded
    ///     fine (RX 9070 XT field report; same defect class as
    ///     LizardByte/Sunshine#5385), so a bare-defaults failure alone must not erase
    ///     the encoder. <c>-qp_i/-qp_p</c> are H.264/HEVC-only options and must stay
    ///     off the AV1 attempt.
    /// </summary>
    [Theory]
    [InlineData("hevc_amf", true)]
    [InlineData("h264_amf", true)]
    [InlineData("av1_amf", false)]
    public void Amf_probe_tries_bare_then_explicit_session(string encoder, bool expectQpFlags)
    {
        var attempts = TranscodingService.BuildEncoderProbeAttempts("-hwaccel auto", encoder);

        attempts.Should().HaveCount(2);

        var (bareArgs, bareVariant)         = attempts[0];
        var (explicitArgs, explicitVariant) = attempts[1];

        // Bare goes first — it's the historical probe, and a pass skips the second run.
        bareVariant.Should().BeEmpty();
        bareArgs.Should().NotContain("-usage").And.NotContain("-quality").And.NotContain("-qp_i");

        explicitVariant.Should().Be("explicit_session");
        explicitArgs.Should().Contain("-usage transcoding -quality quality");
        if (expectQpFlags)
            explicitArgs.Should().Contain("-qp_i 24 -qp_p 24");
        else
            explicitArgs.Should().NotContain("-qp_i").And.NotContain("-qp_p");

        foreach (var (args, _) in attempts)
        {
            args.Should().Contain("-hwaccel auto");
            args.Should().Contain($"-c:v {encoder}");
            args.Should().Contain("-frames:v 1");
            args.Should().NotContain("-low_power");
            args.Should().NotContain("hwupload");
        }
    }

    /// <summary>
    ///     Other non-VAAPI encoders keep the single bare attempt: no CQP flags, no
    ///     hwupload filter, and never <c>-low_power</c> (QSV's own low_power option
    ///     defaults to auto — the runtime negotiates it, unlike VAAPI).
    /// </summary>
    [Theory]
    [InlineData("hevc_qsv",  "-hwaccel qsv -qsv_device /dev/dri/renderD128")]
    [InlineData("hevc_nvenc", "-hwaccel cuda")]
    [InlineData("hevc_videotoolbox", "-hwaccel videotoolbox")]
    public void Non_vaapi_probe_is_a_single_bare_attempt(string encoder, string hwFlags)
    {
        var attempts = TranscodingService.BuildEncoderProbeAttempts(hwFlags, encoder);

        attempts.Should().HaveCount(1);
        var (args, variant) = attempts[0];

        variant.Should().BeEmpty();
        args.Should().Contain(hwFlags);
        args.Should().Contain($"-c:v {encoder}");
        args.Should().Contain("-frames:v 1");
        args.Should().NotContain("-low_power");
        args.Should().NotContain("-rc_mode");
        args.Should().NotContain("hwupload");
    }
}
