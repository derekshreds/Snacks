using System.Runtime.InteropServices;
using SkiaSharp;

namespace Snacks.Services.Ocr;

/// <summary>
///     Turns a raw PGS/VobSub RGBA bitmap into clean black-on-white PNG bytes ready
///     for Tesseract. Replaces the older alpha-threshold pipeline with a principled
///     document-OCR chain recommended in production subtitle-OCR tools.
/// </summary>
/// <remarks>
///     Pipeline:
///     <list type="number">
///         <item><b>Connected-component body extraction</b> — flood-fill the alpha-opaque
///               region, compute per-component median α and luma, keep only pixels that
///               cluster with their component's consensus. Drops outline, shadow, halo,
///               and small antialiasing fragments by construction.</item>
///         <item><b>Aggressive upscale</b> — typical subtitle glyphs are 40-60 px tall
///               (~140-180 DPI after moderate scaling). Tesseract's LSTM expects ~300 DPI,
///               so an 6× upscale with SkiaSharp's high-quality filter is the sweet spot.</item>
///         <item><b>Optional italic deshear</b> — per-cue, moment-based skew estimation
///               (eigendecomposition of the foreground-pixel covariance matrix). Far more
///               robust than column-variance on stylised or sparse text.</item>
///         <item><b>Sauvola local thresholding</b> — adaptive binarisation that handles
///               antialiased edges, stroke-width variation, and uneven rendering without a
///               global threshold. Produces crisp binary edges for the LSTM.</item>
///     </list>
/// </remarks>
public static class ImagePreprocessor
{
    /// <param name="rgba">    Raw pixels, 4 bytes per pixel, top-down. </param>
    /// <param name="width">   Pixel width. </param>
    /// <param name="height">  Pixel height. </param>
    /// <param name="upscale"> Integer upscale factor. 6× is the default for PGS — it brings
    ///                        40-60 px tall glyphs into Tesseract's ~300 DPI sweet spot. </param>
    /// <param name="skewRad"> Shear angle to apply (radians) from a per-cue detection pass.
    ///                        Pass 0 for upright. </param>
    public static byte[] Preprocess(ReadOnlySpan<byte> rgba, int width, int height, int upscale = 6, double skewRad = 0)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Invalid bitmap dimensions.");

        // Extract body via connected components + component-local consensus.
        var mask = ExtractBodyMask(rgba, width, height);

        // Build the Skia source bitmap from the body mask.
        using var maskBmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        Marshal.Copy(mask, 0, maskBmp.GetPixels(), mask.Length);
        maskBmp.NotifyPixelsChanged();

        int factor = Math.Max(1, upscale);
        int upW    = width  * factor;
        int upH    = height * factor;

        // Shear padding — when the caller supplied a skew angle we need extra width to
        // hold the sheared output.
        int shearPad = skewRad != 0 ? (int)Math.Ceiling(upH * Math.Abs(Math.Tan(skewRad))) : 0;
        int outW     = upW + shearPad;

        // Pre-upscale blur on the binary mask. A 3×3 Gaussian converts the hard
        // 0/255 edges into graded 1-pixel transitions, which bicubic upscale handles
        // cleanly. Without this, bicubic on a hard binary produces the stair-step
        // aliasing ("waviness") you'd see along curves at 6× scale.
        var soft = Gaussian3x3(mask, width, height);
        using var softBmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        Marshal.Copy(soft, 0, softBmp.GetPixels(), soft.Length);
        softBmp.NotifyPixelsChanged();

        // Upscale (and optionally deshear) with SkiaSharp's high-quality filter.
        using var scaled = new SKBitmap(new SKImageInfo(outW, upH, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint
            {
                IsAntialias   = true,
                FilterQuality = SKFilterQuality.High,
            };
            if (skewRad != 0)
            {
                float skewX = (float)Math.Tan(skewRad);
                float txPx  = skewX < 0 ? -skewX * height * factor : 0;
                var m = SKMatrix.CreateTranslation(txPx, 0);
                m = m.PreConcat(SKMatrix.CreateScale(factor, factor));
                m = m.PreConcat(SKMatrix.CreateSkew(skewX, 0));
                canvas.SetMatrix(m);
                canvas.DrawBitmap(softBmp, 0, 0, paint);
            }
            else
            {
                canvas.DrawBitmap(softBmp, new SKRect(0, 0, upW, upH), paint);
            }
        }

        // Invert — body pixels in the upscaled mask are bright; Tesseract wants dark
        // text on white.
        var pixels   = scaled.Bytes;
        int total    = outW * upH;
        var inverted = new byte[total];
        for (int i = 0; i < total; i++) inverted[i] = (byte)(255 - pixels[i]);

        // Another Gaussian pass on the upscaled grayscale to soak up any residual
        // staircase from the upscaler. Radius-1 blur is enough — larger would blur
        // thin strokes like 'i' and 'l'.
        var smoothed = Gaussian3x3(inverted, outW, upH);

        // Simple global threshold. Sauvola used to live here, but on a uniformly-lit
        // body mask its local stats misclassify filled-letter interiors as "background"
        // relative to their own mean, producing hollow-letter output. A fixed 128
        // threshold is unambiguous: the input is a smoothed grayscale body mask so
        // anything below mid-gray is text, anything above is background.
        var binarised = new byte[smoothed.Length];
        for (int i = 0; i < smoothed.Length; i++) binarised[i] = smoothed[i] < 128 ? (byte)0 : (byte)255;

        using var result = new SKBitmap(new SKImageInfo(outW, upH, SKColorType.Gray8, SKAlphaType.Opaque));
        Marshal.Copy(binarised, 0, result.GetPixels(), total);
        result.NotifyPixelsChanged();

        using var image = SKImage.FromBitmap(result);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // =================================================================================
    // Skew detection — moment-based (eigendecomposition of the foreground covariance)
    // =================================================================================

    /// <summary>
    ///     Estimates the italic shear angle (radians) for a single cue by shearing the
    ///     body mask at a range of candidate angles and picking the one that maximises
    ///     the variance of the column-wise foreground-pixel counts. Intuition: at the
    ///     correct italic angle, all vertical strokes collapse into tight columns, so
    ///     the column histogram has sharp peaks (high variance); at any wrong angle
    ///     the strokes smear across more columns (low variance).
    /// </summary>
    /// <remarks>
    ///     Column-variance is the right signal here because it measures alignment in
    ///     the axis we actually care about (vertical stroke columns). Moment-based
    ///     covariance is dominated by inter-letter horizontal spacing on full-line
    ///     cues, which dilutes the italic correlation and produces tiny readings —
    ///     typically 1-2° instead of the actual 12-15°, below any reasonable gate.
    /// </remarks>
    public static double DetectPerCueSkew(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return 0;

        var mask = ExtractBodyMask(rgba, width, height);

        // Require a minimum amount of foreground — very short cues don't have enough
        // vertical strokes to give a reliable variance peak.
        int foreground = 0;
        for (int i = 0; i < mask.Length; i++) if (mask[i] > 128) foreground++;
        if (foreground < 300) return 0;

        // Sweep ±18° in 2° steps. Caching all variances lets us interpolate between
        // the peak and its neighbours for sub-step precision.
        const int STEP = 2;
        const int MAX_DEG = 18;
        int slots = 2 * MAX_DEG / STEP + 1;           // -18, -16, ..., 0, ..., 16, 18
        var vars = new double[slots];
        for (int i = 0; i < slots; i++)
        {
            int deg = -MAX_DEG + i * STEP;
            double rad = deg * Math.PI / 180.0;
            vars[i] = ShearedColumnVariance(mask, width, height, rad);
        }
        int uprightIdx = MAX_DEG / STEP;
        double uprightVar = vars[uprightIdx];
        if (uprightVar <= 0) return 0;

        int bestIdx = uprightIdx;
        for (int i = 0; i < slots; i++) if (vars[i] > vars[bestIdx]) bestIdx = i;
        if (bestIdx == uprightIdx) return 0;

        int bestDeg = -MAX_DEG + bestIdx * STEP;
        double bestVar = vars[bestIdx];

        // Gates BEFORE refinement — they're meant to reject upright cues that happen
        // to have slightly elevated variance at a non-zero sample due to stroke-shape
        // noise, not to validate a plausible italic. Thresholds:
        //   • 20% variance gain over upright — real italic produces a dramatic peak;
        //     10% was catching stylised-but-upright Tron-style text.
        //   • Coarse peak angle |≥ 4°| — italic fonts are at least 8-10°, so a peak
        //     at the nearest integer step in the sweep should already be at ≥4°.
        if (bestVar < uprightVar * 1.20) return 0;
        if (Math.Abs(bestDeg) < 4)       return 0;

        // Parabolic peak refinement: fit a parabola to the three variance samples
        // around bestIdx to get the sub-step offset of the real peak. Typically
        // corrects a 1°-2° overshoot caused by the coarse 2° sweep.
        double refinedDeg = bestDeg;
        if (bestIdx > 0 && bestIdx < slots - 1)
        {
            double vL = vars[bestIdx - 1];
            double vP = vars[bestIdx];
            double vR = vars[bestIdx + 1];
            double denom = vL + vR - 2 * vP;
            if (Math.Abs(denom) > 1e-9)
            {
                double offset = STEP * (vL - vR) / (2 * denom);
                if (Math.Abs(offset) <= STEP) refinedDeg = bestDeg + offset;
            }
        }

        // Final sanity on the refined angle — still must be in the plausible italic
        // range. If parabolic refinement drags the peak back toward 0° / past ±18°,
        // reject the whole detection.
        double absDeg = Math.Abs(refinedDeg);
        if (absDeg < 3.0 || absDeg > 18.0) return 0;
        return refinedDeg * Math.PI / 180.0;
    }

    // =================================================================================
    // Body extraction — connected components + per-component consensus
    // =================================================================================

    /// <summary>
    ///     Returns a binary 0/255 mask containing only the letter-body pixels. Outline,
    ///     shadow, halo, and antialiased-edge pixels are excluded.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Algorithm: horizontally scan a band of rows through the vertical centre of
    ///         the text. Inside every contiguous opaque run on each row, <b>skip the
    ///         outline at both ends of the run</b> and sample the deep interior pixels.
    ///         The mode of those interior samples is, by construction, the true body
    ///         colour — sampled from inside each letter stroke rather than averaged over
    ///         body+outline together.
    ///     </para>
    ///     <para>
    ///         Previous approaches (Otsu, connected components + median) all failed the
    ///         same way: body and outline are topologically connected in the alpha mask,
    ///         so any statistic taken across the whole region averages them. The
    ///         scan-and-skip-outline method only ever samples pixels that are provably
    ///         interior — there are at least a few outline pixels between each sample
    ///         and the nearest transparent edge.
    ///     </para>
    /// </remarks>
    internal static byte[] ExtractBodyMask(ReadOnlySpan<byte> rgba, int width, int height)
    {
        const int COLOR_TOLERANCE = 30;
        int n = width * height;

        // Cache α and luma per pixel.
        var alpha = new byte[n];
        var luma  = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int pos = i * 4;
            alpha[i] = rgba[pos + 3];
            luma[i]  = Luma(rgba[pos], rgba[pos + 1], rgba[pos + 2]);
        }

        // Text vertical span. A pixel counts as "not transparent" iff α > 0 — the
        // scan doesn't use an alpha threshold because halos/shadows/aa-edges are all
        // part of the cross-section pattern and we identify the body purely by color.
        int topRow = -1, botRow = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (alpha[row + x] > 0) { if (topRow < 0) topRow = y; botRow = y; break; }
            }
        }
        if (topRow < 0) return new byte[n];

        // Scan a band of rows around the vertical midpoint. Inside each non-transparent
        // run we look for the BRIGHTEST plateau — that's the body. Drop shadows break
        // the "innermost" assumption because they extend to the bottom-right only, so
        // the center of the run shifts off the body; but every subtitle style we care
        // about renders the body brighter than both the outline and the shadow, so the
        // brightest pixels in the run are always the body.
        //
        // Cross-section pattern (symmetric):
        //   [transparent] [grey] [outline] [BODY] [outline] [grey] [transparent]
        // With drop shadow on the right:
        //   [transparent] [outline] [BODY] [outline] [shadow] [shadow] [transparent]
        // In both, max-luma(run) ∈ BODY.
        int midRow  = (topRow + botRow) / 2;
        int textH   = botRow - topRow + 1;
        int bandRad = Math.Max(1, textH / 6);

        Span<int> lumaHist = stackalloc int[256];
        int sampleCount = 0;

        for (int y = Math.Max(topRow, midRow - bandRad); y <= Math.Min(botRow, midRow + bandRad); y++)
        {
            int row = y * width;
            int x = 0;
            while (x < width)
            {
                while (x < width && alpha[row + x] == 0) x++;
                if (x >= width) break;
                int segStart = x;
                while (x < width && alpha[row + x] > 0) x++;
                int segEnd = x - 1;

                if (segEnd - segStart < 2) continue;

                // Find the peak luma in this run — candidate body brightness.
                byte peak = 0;
                for (int sx = segStart; sx <= segEnd; sx++)
                    if (luma[row + sx] > peak) peak = luma[row + sx];
                int bright = Math.Max(0, peak - 40); // pixels this bright or brighter count as body-candidate

                // Scan the run looking for BRIGHT BLOCKS SANDWICHED BY DARKER PIXELS
                // ON BOTH SIDES. That's the defining signature of a letter body in a
                // cross-section: the body plateau has outline/shadow/halo immediately
                // before it (within the same opaque run) AND after it. This is what
                // rejects runs that are entirely outline/shadow (e.g. horizontal scan
                // through the hole of an 'o'), which otherwise pollute the body-luma
                // histogram with non-body samples.
                bool seenDarkBefore = false;
                int  i = segStart;
                while (i <= segEnd)
                {
                    if (luma[row + i] < bright)
                    {
                        seenDarkBefore = true;
                        i++;
                        continue;
                    }
                    int blockStart = i;
                    while (i <= segEnd && luma[row + i] >= bright) i++;
                    bool seenDarkAfter = i <= segEnd; // we stopped because we hit a darker pixel (not end of run)

                    if (seenDarkBefore && seenDarkAfter)
                    {
                        for (int sx = blockStart; sx < i; sx++)
                        {
                            lumaHist[luma[row + sx]]++;
                            sampleCount++;
                        }
                    }
                }
            }
        }

        // Fallback: if the band yielded no samples (cue is effectively blank), sample
        // every non-transparent pixel. Worse than the center-scan but keeps us from
        // returning a totally blank mask on an edge-case cue.
        if (sampleCount == 0)
        {
            for (int i = 0; i < n; i++) if (alpha[i] > 0) lumaHist[luma[i]]++;
        }

        // Body color = mode of center samples.
        int bodyLuma = 0, bodyMax = 0;
        for (int t = 0; t < 256; t++) if (lumaHist[t] > bodyMax) { bodyMax = lumaHist[t]; bodyLuma = t; }

        // Build mask: any non-transparent pixel whose color is the body color is
        // kept. Outline pixels (different luma cluster) excluded. Shadows and halos
        // with different luma excluded. Fully-transparent pixels excluded by their α=0.
        int lo = Math.Max(0,   bodyLuma - COLOR_TOLERANCE);
        int hi = Math.Min(255, bodyLuma + COLOR_TOLERANCE);
        var mask = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (alpha[i] == 0) continue;
            if (luma[i] < lo || luma[i] > hi) continue;
            mask[i] = 255;
        }
        return mask;
    }

    // =================================================================================
    // Sauvola local thresholding
    // =================================================================================

    /// <summary>
    ///     Sauvola's adaptive local thresholding: for each pixel, compute mean and
    ///     std-deviation in a window, threshold at <c>mean · (1 + k·(stddev/R − 1))</c>
    ///     with k=0.2 and R=128 (standard values for document binarisation). Produces
    ///     clean binary output that adapts to local contrast, which a global threshold
    ///     (like Otsu or fixed 128) cannot do when stroke widths or antialiasing vary
    ///     across the image.
    /// </summary>
    /// <remarks>
    ///     Computes per-pixel mean and variance in O(W·H) using separable 1-D box filters
    ///     over integral images. Independent of window size, which is important because
    ///     the window scales with text height.
    /// </remarks>
    private static byte[] SauvolaThreshold(byte[] gray, int w, int h)
    {
        // Window radius scales with image height so a single 40 px cue and a 300 px
        // upscaled one both use sensibly-sized windows.
        int radius = Math.Max(8, Math.Min(w, h) / 8);
        int size   = 2 * radius + 1;

        // Integral images for fast box-filter mean + variance.
        var integ  = new long[(w + 1) * (h + 1)];
        var integ2 = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0, rowSum2 = 0;
            for (int x = 0; x < w; x++)
            {
                byte v = gray[y * w + x];
                rowSum  += v;
                rowSum2 += (long)v * v;
                integ [(y + 1) * (w + 1) + x + 1] = integ [y * (w + 1) + x + 1] + rowSum;
                integ2[(y + 1) * (w + 1) + x + 1] = integ2[y * (w + 1) + x + 1] + rowSum2;
            }
        }

        const double K = 0.2;
        const double R = 128.0;
        var outBytes = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(w - 1, x + radius);
                long count = (long)(y1 - y0 + 1) * (x1 - x0 + 1);

                long sum  = integ [(y1 + 1) * (w + 1) + x1 + 1]
                          - integ [y0       * (w + 1) + x1 + 1]
                          - integ [(y1 + 1) * (w + 1) + x0      ]
                          + integ [y0       * (w + 1) + x0      ];
                long sum2 = integ2[(y1 + 1) * (w + 1) + x1 + 1]
                          - integ2[y0       * (w + 1) + x1 + 1]
                          - integ2[(y1 + 1) * (w + 1) + x0      ]
                          + integ2[y0       * (w + 1) + x0      ];

                double mean = (double)sum / count;
                double var  = (double)sum2 / count - mean * mean;
                if (var < 0) var = 0;
                double std = Math.Sqrt(var);
                double threshold = mean * (1.0 + K * (std / R - 1.0));

                outBytes[y * w + x] = gray[y * w + x] > threshold ? (byte)255 : (byte)0;
            }
        }
        return outBytes;
    }

    // =================================================================================
    // Utility: column-sum variance (kept for legacy callers, unused by the new pipeline)
    // =================================================================================

    internal static double ShearedColumnVariance(byte[] mask, int width, int height, double skewRad)
    {
        double shear = Math.Tan(skewRad);
        int    pad   = (int)Math.Ceiling(Math.Abs(shear) * height) + 1;
        int    cols  = width + 2 * pad;
        var    sums  = new int[cols];

        for (int y = 0; y < height; y++)
        {
            int xOff = (int)(shear * (y - height / 2)) + pad;
            int row  = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x] <= 128) continue;
                int dst = x + xOff;
                if ((uint)dst < (uint)cols) sums[dst]++;
            }
        }

        double total = 0;
        foreach (var s in sums) total += s;
        if (total < 20) return 0;

        double mean = total / cols;
        double var  = 0;
        foreach (var s in sums) var += (s - mean) * (s - mean);
        return var / cols;
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)((2990 * r + 5870 * g + 1140 * b) / 10000);

    /// <summary>
    ///     3×3 Gaussian blur (kernel 1/2/1 horizontally and vertically, normalised to 16).
    ///     Used twice in the pipeline: once before upscaling to give the binary body mask
    ///     1-pixel soft edges so bicubic doesn't stair-step, and once after upscaling to
    ///     mop up any residual aliasing before Sauvola binarises the result.
    /// </summary>
    private static byte[] Gaussian3x3(byte[] src, int w, int h)
    {
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        // Horizontal pass: 1/4, 2/4, 1/4.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int a = src[row + (x > 0         ? x - 1 : x)];
                int b = src[row + x];
                int c = src[row + (x < w - 1 ? x + 1 : x)];
                tmp[row + x] = (byte)((a + 2 * b + c + 2) >> 2);
            }
        }
        // Vertical pass on the horizontally-blurred buffer.
        for (int y = 0; y < h; y++)
        {
            int rowM1 = (y > 0         ? y - 1 : y) * w;
            int rowC  = y * w;
            int rowP1 = (y < h - 1 ? y + 1 : y) * w;
            for (int x = 0; x < w; x++)
            {
                int a = tmp[rowM1 + x];
                int b = tmp[rowC  + x];
                int c = tmp[rowP1 + x];
                dst[rowC + x] = (byte)((a + 2 * b + c + 2) >> 2);
            }
        }
        return dst;
    }

    /// <summary>
    ///     Encodes raw RGBA pixels directly to PNG with no preprocessing.
    /// </summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> rgba, int width, int height)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var rgbaArr = rgba.ToArray();
        Marshal.Copy(rgbaArr, 0, bmp.GetPixels(), width * height * 4);
        bmp.NotifyPixelsChanged();
        using var image = SKImage.FromBitmap(bmp);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
