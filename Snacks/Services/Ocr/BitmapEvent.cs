namespace Snacks.Services.Ocr;

/// <summary>
///     One rendered subtitle cue produced by a bitmap-subtitle parser: a timestamped
///     raw RGBA bitmap ready for the preprocessing + OCR pipeline to consume.
/// </summary>
/// <remarks>
///     Parsers emit raw RGBA (not PNG, not pre-binarized) so <see cref="NativeOcrService"/>
///     can run a file-wide skew-detection pass across all cues before applying a single
///     consistent deshear transform to each one. Per-cue skew detection was unreliable
///     because short cues have too little text to measure a stable angle.
/// </remarks>
/// <param name="Start">  Presentation time of the first frame the cue is visible. </param>
/// <param name="End">    Presentation time of the last frame the cue is visible. </param>
/// <param name="Rgba">   Raw RGBA pixels, 4 bytes per pixel, top-down, length = Width×Height×4. </param>
/// <param name="Width">  Pixel width of the cue bitmap. </param>
/// <param name="Height"> Pixel height of the cue bitmap. </param>
public sealed record BitmapEvent(TimeSpan Start, TimeSpan End, byte[] Rgba, int Width, int Height);
