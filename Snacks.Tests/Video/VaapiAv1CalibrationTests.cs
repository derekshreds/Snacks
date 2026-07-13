using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for the encoder-aware VAAPI quality scale and the CQP calibration
///     planner (<see cref="TranscodingService.PlanNextCalibrationQp"/>).
///
///     Regression coverage for the AMD/AV1 "no savings" bug: av1_vaapi maps
///     <c>-global_quality</c> to the AV1 quantizer index (1–255), not the 0–51
///     H.264/HEVC QP scale. Calibration used to clamp the search to 18–51, which
///     on AV1 is near-lossless quality — a Blu-ray remux encoded at roughly source
///     bitrate and every conversion was deleted as "no savings realized".
/// </summary>
public sealed class VaapiAv1CalibrationTests
{
    // Log-linear qindex→peak-bitrate model fitted from a real av1_vaapi calibration
    // log on AMD RDNA3 (1080p Blu-ray remux, 29.6Mbps source): global_quality 24
    // measured 38680kbps peak and 44 measured 21882kbps peak.
    private static long Av1RemuxCurve(int qindex) =>
        (long)(38680.0 * Math.Exp(-0.02848 * (qindex - 24)));

    /// <summary>
    ///     Mirrors the accept/plan cycle of CalibrateVaapiQualityAsync with a modeled
    ///     encoder instead of real ffmpeg test encodes.
    /// </summary>
    private static (int qp, bool withinTolerance, int passes) SimulateCalibration(
        Func<int, long> peakKbpsAt, long targetKbps, TranscodingService.VaapiQualityScale scale,
        int maxIterations = 6, double tolerance = 0.15)
    {
        var tested = new Dictionary<int, long>();
        int currentQp = scale.Start;

        for (int i = 1; i <= maxIterations; i++)
        {
            long peak = peakKbpsAt(currentQp);
            tested[currentQp] = peak;

            double ratio = (double)peak / targetKbps;
            if (ratio >= 1 - tolerance && ratio <= 1 + tolerance)
                return (currentQp, true, i);
            if (peak <= targetKbps && currentQp <= scale.Min)
                return (currentQp, true, i);

            var (nextQp, _) = TranscodingService.PlanNextCalibrationQp(tested, currentQp, peak, targetKbps, scale);
            if (nextQp is null) break;
            currentQp = nextQp.Value;
        }

        // "Select best observed" fallback, as in the real loop.
        long upperBound = (long)(targetKbps * (1 + tolerance));
        var underBound = tested.Where(kv => kv.Value <= upperBound).ToList();
        int chosen = underBound.Count > 0
            ? underBound.OrderBy(kv => kv.Key).First().Key
            : tested.OrderBy(kv => Math.Abs(kv.Value - targetKbps)).First().Key;
        return (chosen, false, tested.Count);
    }


    // =====================================================================
    //  Scale selection per encoder.
    // =====================================================================

    [Fact]
    public void Av1_vaapi_uses_qindex_scale()
    {
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");

        scale.Max.Should().Be(255);
        // Search must be able to start and roam well above the 0–51 QP range,
        // otherwise high-bitrate sources can never reach a low bitrate target.
        scale.Start.Should().BeGreaterThan(51);
        scale.Min.Should().BeGreaterThan(51);
        scale.MaxStep.Should().BeGreaterThan(4);
        scale.FixedCqp.Should().BeGreaterThan(51);
    }


    [Theory]
    [InlineData("hevc_vaapi")]
    [InlineData("h264_vaapi")]
    public void H264_and_hevc_vaapi_keep_the_51_scale(string encoder)
    {
        var scale = TranscodingService.VaapiQualityScale.For(encoder);

        scale.Should().Be(new TranscodingService.VaapiQualityScale(
            Start: 24, Min: 18, Max: 51, MaxStep: 4, FixedCqp: 25));
    }


    [Fact]
    public void Av1_vaapi_forced_reencode_uses_qindex_cqp()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "av1_vaapi", useVaapi: true, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-rc_mode CQP");
        flags.Should().Contain("-global_quality:v 125");
        flags.Should().NotContain("-b:v");
    }


    // =====================================================================
    //  Calibration convergence on the customer-reported AV1 curve.
    // =====================================================================

    [Fact]
    public void Av1_calibration_converges_to_target_within_pass_budget()
    {
        const long target = 3500;
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");

        var (qp, withinTolerance, passes) = SimulateCalibration(Av1RemuxCurve, target, scale);

        withinTolerance.Should().BeTrue("the qindex scale can reach 3500kbps on a 29.6Mbps remux");
        passes.Should().BeLessThanOrEqualTo(6);
        Av1RemuxCurve(qp).Should().BeInRange((long)(target * 0.85), (long)(target * 1.15));
    }


    [Fact]
    public void Old_51_scale_could_never_reach_target_on_av1()
    {
        // The pre-fix behavior: searching 18–51 on the AV1 qindex scale. The best
        // reachable bitrate stays several times above target — this is the exact
        // failure from the field report ("no savings realized", conversion deleted).
        const long target = 3500;
        var oldScale = new TranscodingService.VaapiQualityScale(
            Start: 24, Min: 18, Max: 51, MaxStep: 4, FixedCqp: 25);

        var (qp, withinTolerance, _) = SimulateCalibration(Av1RemuxCurve, target, oldScale);

        withinTolerance.Should().BeFalse();
        Av1RemuxCurve(qp).Should().BeGreaterThan(target * 5);
    }


    // =====================================================================
    //  Planner mechanics.
    // =====================================================================

    [Fact]
    public void Planner_first_step_matches_previous_hevc_behavior()
    {
        // 8000kbps measured at QP 24 vs 3500 target used to extrapolate to QP 28
        // (default slope, step capped at +4). The scale refactor must not change it.
        var scale = TranscodingService.VaapiQualityScale.For("hevc_vaapi");
        var tested = new Dictionary<int, long> { [24] = 8000 };

        var (nextQp, reason) = TranscodingService.PlanNextCalibrationQp(tested, 24, 8000, 3500, scale);

        reason.Should().BeNull();
        nextQp.Should().Be(28);
    }


    [Fact]
    public void Planner_extrapolation_is_capped_to_max_step()
    {
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");
        // 10x over target wants a huge jump; must be clamped to +MaxStep.
        var tested = new Dictionary<int, long> { [scale.Start] = 35000 };

        var (nextQp, _) = TranscodingService.PlanNextCalibrationQp(
            tested, scale.Start, 35000, 3500, scale);

        nextQp.Should().Be(scale.Start + scale.MaxStep);
    }


    [Fact]
    public void Planner_bisects_when_target_is_bracketed()
    {
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");
        var tested = new Dictionary<int, long> { [120] = 8000, [160] = 2000 };

        var (nextQp, _) = TranscodingService.PlanNextCalibrationQp(tested, 160, 2000, 3500, scale);

        nextQp.Should().Be(140);
    }


    [Fact]
    public void Planner_reports_convergence_on_adjacent_bracket()
    {
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");
        var tested = new Dictionary<int, long> { [140] = 4000, [141] = 3000 };

        var (nextQp, reason) = TranscodingService.PlanNextCalibrationQp(tested, 141, 3000, 3500, scale);

        nextQp.Should().BeNull();
        reason.Should().Contain("adjacent");
    }


    [Fact]
    public void Planner_never_leaves_the_scale_bounds()
    {
        var scale = TranscodingService.VaapiQualityScale.For("av1_vaapi");
        // Way below target at the top of the range — planner must clamp, not overflow.
        var tested = new Dictionary<int, long> { [250] = 100 };

        var (nextQp, _) = TranscodingService.PlanNextCalibrationQp(tested, 250, 100, 3500, scale);

        nextQp.Should().NotBeNull();
        nextQp!.Value.Should().BeInRange(scale.Min, scale.Max);
    }
}
