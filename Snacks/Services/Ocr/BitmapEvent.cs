namespace Snacks.Services.Ocr;

/// <summary>
///     One rendered subtitle cue produced by a bitmap-subtitle parser: a timestamped
///     RGBA image (PNG-encoded) ready to be handed to the OCR engine.
/// </summary>
/// <param name="Start">    Presentation time of the first frame the cue is visible. </param>
/// <param name="End">      Presentation time of the last frame the cue is visible. </param>
/// <param name="PngBytes"> PNG-encoded image bytes (RGBA, pre-preprocessed if the parser chose to). </param>
public sealed record BitmapEvent(TimeSpan Start, TimeSpan End, byte[] PngBytes);
