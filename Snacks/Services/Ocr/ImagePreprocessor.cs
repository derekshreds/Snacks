using System.Runtime.InteropServices;
using SkiaSharp;

namespace Snacks.Services.Ocr;

/// <summary>
///     Turns a raw RGBA subtitle bitmap into something Tesseract actually likes:
///     optional upscale, grayscale, Otsu-binarize, invert-if-dark-on-light so the
///     text is always black-on-white. The output is PNG bytes.
/// </summary>
public static class ImagePreprocessor
{
    /// <param name="rgba">    Raw pixels, 4 bytes per pixel, top-down. </param>
    /// <param name="width">   Pixel width. </param>
    /// <param name="height">  Pixel height. </param>
    /// <param name="upscale"> Integer upscale factor — 1 = none, 2 = 2×, etc. VobSub benefits from 3-4×. </param>
    public static byte[] Preprocess(ReadOnlySpan<byte> rgba, int width, int height, int upscale = 2)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Invalid bitmap dimensions.");

        using var src = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        // SKBitmap.GetPixels() returns the native pixel buffer; copy into it via Marshal
        // instead of using unsafe/fixed so we stay in verifiable code.
        var rgbaArr = rgba.ToArray();
        Marshal.Copy(rgbaArr, 0, src.GetPixels(), width * height * 4);

        int upW = width  * Math.Max(1, upscale);
        int upH = height * Math.Max(1, upscale);

        using var scaled = new SKBitmap(new SKImageInfo(upW, upH, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.White);
            // Flatten alpha onto white so transparent pixels look like paper background.
            using var paint = new SKPaint
            {
                IsAntialias   = true,
                FilterQuality = SKFilterQuality.High,
            };
            canvas.DrawBitmap(src, new SKRect(0, 0, upW, upH), paint);
        }

        // Convert to pure gray bytes for threshold work.
        int   stride = scaled.RowBytes;
        var   pixels = scaled.Bytes;
        int   total  = upW * upH;

        // Otsu's method: pick the threshold that maximises between-class variance.
        Span<int> hist = stackalloc int[256];
        for (int i = 0; i < total; i++) hist[pixels[i]]++;

        long sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * hist[i];

        long sumB = 0;
        int  wB = 0, wF;
        double maxVar = 0;
        int    threshold = 127;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            wF = total - wB;
            if (wF == 0) break;

            sumB += (long)i * hist[i];
            double mB = (double)sumB / wB;
            double mF = (double)(sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar)
            {
                maxVar = between;
                threshold = i;
            }
        }

        // Count dark vs light pixels to decide if we need to invert (OCR wants black-on-white).
        int dark = 0;
        for (int i = 0; i < total; i++) if (pixels[i] < threshold) dark++;
        bool invert = dark > total / 2;

        var outBytes = new byte[total];
        for (int i = 0; i < total; i++)
        {
            bool isText = pixels[i] < threshold;
            if (invert) isText = !isText;
            outBytes[i] = isText ? (byte)0 : (byte)255;
        }

        using var binarized = new SKBitmap(new SKImageInfo(upW, upH, SKColorType.Gray8, SKAlphaType.Opaque));
        Marshal.Copy(outBytes, 0, binarized.GetPixels(), total);
        binarized.NotifyPixelsChanged();

        using var image = SKImage.FromBitmap(binarized);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    ///     Encodes raw RGBA pixels directly to PNG with no preprocessing. Used when a parser
    ///     wants to emit the original composed bitmap (e.g. PGS renders are usually already
    ///     high-contrast white-on-transparent).
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
