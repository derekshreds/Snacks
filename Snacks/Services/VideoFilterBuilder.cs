namespace Snacks.Services;

/// <summary>
///     Assembles the FFmpeg <c>-vf</c> filter chain for a transcode job. Handles
///     crop, HDR→SDR tonemap, downscale, and the VAAPI <c>format=…|vaapi,hwupload</c>
///     terminator. The caller passes individual filter expressions and the builder
///     orders and joins them into a single chain.
/// </summary>
/// <remarks>
///     Design notes:
///     <list type="bullet">
///         <item>
///             When every filter input is empty/false, <see cref="Emit"/> returns
///             the byte-identical string that was emitted before this class existed:
///             either <c>""</c> (no filters, or VAAPI with hardware decode) or
///             <c>"-vf format={fmt}|vaapi,hwupload "</c> (VAAPI with software decode).
///         </item>
///         <item>
///             User-chosen strategy is "SW filters + hwupload". When any filter is
///             active on a VAAPI path, the caller is expected to suppress hardware
///             decode (so the filters run on CPU frames) and pass <c>canHwDecode=false</c>.
///             The builder does not enforce this; it simply emits the SW filter chain
///             followed by <c>format=…|vaapi,hwupload</c> when VAAPI is the target.
///         </item>
///         <item>
///             Filter order: crop → tonemap → scale → hwupload-terminator. Cropping
///             first reduces pixels through the rest of the chain.
///         </item>
///     </list>
/// </remarks>
public static class VideoFilterBuilder
{
    /// <summary> Builds the <c>-vf …</c> fragment (including trailing space) or returns <c>""</c>. </summary>
    /// <param name="cropExpr">    Crop filter expression without the <c>-vf </c> prefix (e.g. <c>"crop=1920:800:0:140"</c>), or <c>null</c>. </param>
    /// <param name="tonemap">     When <c>true</c>, inserts the zscale/tonemap chain (for HDR→SDR). </param>
    /// <param name="scaleExpr">   Scale filter expression (e.g. <c>"scale=w=-2:h=1080:flags=lanczos"</c>), or <c>null</c>. </param>
    /// <param name="useVaapi">    When <c>true</c>, the encoder is VAAPI and the chain ends with <c>format=…|vaapi,hwupload</c>. </param>
    /// <param name="canHwDecode"> When <c>true</c>, frames arrive on GPU (VAAPI hwaccel); the terminator is omitted because they are already in VAAPI format. </param>
    /// <param name="vaapiFormat"> Pixel format fed to the VAAPI upload (<c>"nv12"</c> or <c>"p010"</c>). </param>
    public static string Emit(
        string? cropExpr,
        bool    tonemap,
        string? scaleExpr,
        bool    useVaapi,
        bool    canHwDecode,
        string  vaapiFormat)
    {
        bool hasFilter = !string.IsNullOrEmpty(cropExpr) || tonemap || !string.IsNullOrEmpty(scaleExpr);

        // No user-requested filters — preserve pre-refactor behavior byte-for-byte.
        if (!hasFilter)
        {
            if (!useVaapi || canHwDecode) return "";
            return $"-vf format={vaapiFormat}|vaapi,hwupload ";
        }

        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(cropExpr))  parts.Add(cropExpr);
        if (tonemap)                          parts.Add(TonemapSwChain);
        if (!string.IsNullOrEmpty(scaleExpr)) parts.Add(scaleExpr);
        if (useVaapi)                         parts.Add($"format={vaapiFormat}|vaapi,hwupload");

        return $"-vf {string.Join(",", parts)} ";
    }

    /// <summary>
    ///     Standard zscale/tonemap recipe for HDR (PQ/HLG) → SDR bt709. Requires an
    ///     ffmpeg build with <c>libzimg</c>. Callers should verify availability; a
    ///     <c>colorspace</c>+<c>tonemap</c> fallback applies when libzimg is absent.
    /// </summary>
    public const string TonemapSwChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>
    ///     Fallback tonemap chain using <c>colorspace</c>+<c>tonemap</c>. Lossier
    ///     than the zscale version but bundled with every stock ffmpeg build.
    /// </summary>
    public const string TonemapSwChainFallback =
        "colorspace=iall=bt2020nc:all=bt709:format=yuv420p,tonemap=tonemap=hable:desat=0";
}
