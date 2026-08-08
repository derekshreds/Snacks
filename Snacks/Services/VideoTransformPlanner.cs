using System.Globalization;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Pure planning helpers for video geometry and frame-rate transforms.
///     Keeping these decisions separate from process execution makes them
///     independently testable and reusable by future encoding pipelines.
/// </summary>
internal static class VideoTransformPlanner
{
    internal static bool WillDownscaleBelow4K(EncoderOptions options)
    {
        if (!IsDownscalePolicyActive(options.DownscalePolicy)) return false;
        return ResolveDownscaleHeight(options.DownscaleTarget) <= 1440;
    }

    internal static bool IsDownscalePolicyActive(string policy) =>
        string.Equals(policy, "Always", StringComparison.OrdinalIgnoreCase)
        || string.Equals(policy, "CapAtTarget", StringComparison.OrdinalIgnoreCase)
        || string.Equals(policy, "IfLarger", StringComparison.OrdinalIgnoreCase);

    internal static int ResolveDownscaleHeight(string target) => target switch
    {
        "4K" => 2160,
        "2160p" => 2160,
        "1440p" => 1440,
        "1080p" => 1080,
        "720p" => 720,
        "480p" => 480,
        "240p" => 240,
        _ => 1080,
    };

    internal static string? ComputeScaleExpr(WorkItem workItem, EncoderOptions options)
    {
        var policy = options.DownscalePolicy;
        if (!IsDownscalePolicyActive(policy)) return null;

        int targetHeight = ResolveDownscaleHeight(options.DownscaleTarget);
        var video = workItem.Probe?.Streams?.FirstOrDefault(stream => stream.CodecType == "video");
        int sourceHeight = video?.Height ?? 0;
        if (sourceHeight <= 0) return null;

        bool always = string.Equals(policy, "Always", StringComparison.OrdinalIgnoreCase);
        if (!always && sourceHeight <= targetHeight) return null;

        return $"scale=w=-2:h={targetHeight}:flags=lanczos";
    }

    internal static string? ComputeFixedFrameFilter(EncoderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FixedFrameSize)) return null;

        var parts = options.FixedFrameSize.ToLowerInvariant().Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        width -= width % 2;
        height -= height % 2;
        if (width <= 0 || height <= 0) return null;

        return $"scale=min(iw\\,{width}):min(ih\\,{height}):force_original_aspect_ratio=decrease,"
               + $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p";
    }

    internal static string? ComputeFpsCapExpr(WorkItem workItem, EncoderOptions options)
    {
        int cap = options.MaxFrameRate;
        if (cap <= 0) return null;

        var video = workItem.Probe?.Streams?.FirstOrDefault(stream => stream.CodecType == "video");
        double? sourceFps = ParseFrameRate(video?.AvgFrameRate) ?? ParseFrameRate(video?.RFrameRate);
        return sourceFps is not null && sourceFps > cap ? $"fps={cap}" : null;
    }

    internal static double? ParseFrameRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return null;

        var parts = rate.Split('/');
        if (parts.Length == 1
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double whole))
        {
            return whole > 0 ? whole : null;
        }

        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
            && numerator > 0
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return null;
    }
}
