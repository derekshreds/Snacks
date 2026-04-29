using System.Diagnostics;
using Snacks.Services.Ocr;

namespace Snacks.Services;

/// <summary>
///     Pre-pass subtitle extraction. For each subtitle stream in the source that
///     passes the language keep-list, writes a sidecar file alongside the planned
///     main output (<c>{basename}.{lang}[.{n}].{srt|ass}</c>). Text streams are
///     transcoded by FFmpeg directly; bitmap streams are handed off to
///     <see cref="NativeOcrService"/> for OCR.
/// </summary>
/// <remarks>
///     The caller is expected to strip all subtitle streams from the main encode
///     (<c>-sn</c>) when extraction is active — the sidecar files replace the
///     muxed subtitle tracks.
/// </remarks>
public sealed class SubtitleExtractionService
{
    private readonly FfprobeService    _ffprobeService;
    private readonly NativeOcrService  _ocr;
    private readonly string            _ffmpegPath;

    public SubtitleExtractionService(FfprobeService ffprobeService, NativeOcrService ocr)
    {
        ArgumentNullException.ThrowIfNull(ffprobeService);
        ArgumentNullException.ThrowIfNull(ocr);
        _ffprobeService = ffprobeService;
        _ocr            = ocr;
        _ffmpegPath     = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    }

    /// <summary>
    ///     Extracts all kept subtitle streams as sidecar files next to <paramref name="sidecarBase"/>.
    ///     Returns the list of sidecar paths actually written (may be empty).
    /// </summary>
    /// <param name="workItem">         The job this extraction belongs to — used for logging via <paramref name="log"/>. </param>
    /// <param name="inputPath">        Source file. </param>
    /// <param name="sidecarBase">      Full path sans extension (e.g. <c>"/out/Movie [snacks]"</c>); language + <c>.srt</c>/<c>.ass</c> appended. </param>
    /// <param name="languagesToKeep">  Two-letter language keep-list; null/empty keeps all. </param>
    /// <param name="format">           <c>"srt"</c> or <c>"ass"</c>. OCR output is always <c>"srt"</c> regardless. </param>
    /// <param name="convertBitmaps">   When <c>true</c>, bitmap streams are OCR'd via Subtitle Edit. </param>
    /// <param name="log">              Async logger callback (usually wired to <c>LogAsync</c>). </param>
    /// <param name="ct">               Cancellation token. </param>
    public async Task<IReadOnlyList<string>> ExtractAsync(
        Models.WorkItem         workItem,
        string                  inputPath,
        string                  sidecarBase,
        IReadOnlyList<string>?  languagesToKeep,
        string                  format,
        bool                    convertBitmaps,
        Func<string, Task>      log,
        CancellationToken       ct)
    {
        if (workItem.Probe == null) return Array.Empty<string>();

        var specs = _ffprobeService.SelectSidecarStreams(workItem.Probe, languagesToKeep, convertBitmaps);
        if (specs.Count == 0)
        {
            await log("Sidecar extraction: no matching subtitle streams — skipping.");
            return Array.Empty<string>();
        }

        string fmt = string.Equals(format, "ass", StringComparison.OrdinalIgnoreCase) ? "ass" : "srt";
        var written = new List<string>();

        // Disambiguate same-language tracks by suffixing .2, .3, … on collision.
        var langCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Acquired lazily on the first bitmap stream and released after the loop,
        // so two parallel encodes don't drive the shared Tesseract engines at the
        // same time. Text streams skip the lock since ffmpeg handles them directly.
        IDisposable? ocrSlot = null;
        try
        {
            foreach (var spec in specs)
            {
                langCounts.TryGetValue(spec.Lang, out int seen);
                langCounts[spec.Lang] = seen + 1;
                string suffix  = seen == 0 ? "" : $".{seen + 1}";

                // OCR output is always .srt — the native OCR path can't produce styled ASS.
                string outFmt   = spec.IsBitmap ? "srt" : fmt;
                // Plex & Jellyfin prefer ISO 639-2/B 3-letter codes in sidecar filenames
                // (Movie.eng.srt). Fall back to whatever tag the source carried when the
                // language isn't in LanguageMatcher's table.
                string langCode = LanguageMatcher.ToThreeLetterB(spec.Lang) ?? spec.Lang;
                string outPath  = $"{sidecarBase}.{langCode}{suffix}.{outFmt}";

                try
                {
                    if (spec.IsBitmap)
                    {
                        ocrSlot ??= await _ocr.AcquireOcrSlotAsync(
                            Path.GetFileName(inputPath), log, ct);
                        var produced = await _ocr.ConvertBitmapToSrtAsync(
                            inputPath, spec.StreamIndex, spec.Lang, spec.CodecName, outPath, log, ct);
                        if (!string.IsNullOrEmpty(produced))
                            written.Add(produced);
                    }
                    else
                    {
                        await ExtractTextStreamAsync(inputPath, spec.StreamIndex, outFmt, outPath, log, ct);
                        if (File.Exists(outPath)) written.Add(outPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Partial sidecar sets are worse than none — delete the completed ones so a
                    // retry starts from a clean state instead of the user thinking they have the
                    // full set from a prior run.
                    foreach (var p in written)
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    await log($"Sidecar extraction failed for stream {spec.StreamIndex} ({spec.Lang}): {ex.Message}");
                }
            }
        }
        finally
        {
            ocrSlot?.Dispose();
        }

        await log($"Sidecar extraction: wrote {written.Count} file(s).");
        return written;
    }

    /// <summary>
    ///     Runs OCR on bitmap subtitle streams and returns the produced <c>.srt</c> files
    ///     so the caller can mux them back into the main encode as text subtitle tracks
    ///     — used when <c>ConvertImageSubtitlesToSrt</c> is on without sidecar extraction.
    /// </summary>
    /// <remarks>
    ///     Text subtitle streams are intentionally ignored here: they stay muxed via
    ///     <c>MapSub</c>'s existing stream-copy path. Only bitmap streams need conversion.
    /// </remarks>
    public async Task<IReadOnlyList<OcrMuxResult>> OcrBitmapsForMuxAsync(
        Models.WorkItem        workItem,
        string                 inputPath,
        string                 tmpDir,
        IReadOnlyList<string>? languagesToKeep,
        Func<string, Task>     log,
        CancellationToken      ct)
    {
        if (workItem.Probe == null) return Array.Empty<OcrMuxResult>();

        var bitmapSpecs = _ffprobeService
            .SelectSidecarStreams(workItem.Probe, languagesToKeep, includeBitmaps: true)
            .Where(s => s.IsBitmap)
            .ToList();

        if (bitmapSpecs.Count == 0)
        {
            await log("OCR-for-mux: no image-based subtitle streams found — nothing to embed.");
            return Array.Empty<OcrMuxResult>();
        }

        Directory.CreateDirectory(tmpDir);
        var results    = new List<OcrMuxResult>();
        var langCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Held for the entire bitmap pass so a parallel encode's OCR work waits
        // until this movie's tracks are all done.
        using var ocrSlot = await _ocr.AcquireOcrSlotAsync(
            Path.GetFileName(inputPath), log, ct);

        foreach (var spec in bitmapSpecs)
        {
            langCounts.TryGetValue(spec.Lang, out int seen);
            langCounts[spec.Lang] = seen + 1;
            string suffix  = seen == 0 ? "" : $".{seen + 1}";
            string outPath = Path.Combine(tmpDir, $"ocr.{spec.Lang}{suffix}.srt");

            try
            {
                var produced = await _ocr.ConvertBitmapToSrtAsync(
                    inputPath, spec.StreamIndex, spec.Lang, spec.CodecName, outPath, log, ct);
                if (!string.IsNullOrEmpty(produced))
                    results.Add(new OcrMuxResult(produced, spec.Lang, spec.Title));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await log($"OCR-for-mux failed for stream {spec.StreamIndex} ({spec.Lang}): {ex.Message}");
            }
        }

        await log($"OCR-for-mux: produced {results.Count} track(s) ready to embed.");
        return results;
    }

    /// <summary> One OCR'd SRT file ready to be added to the main encode as a text subtitle track. </summary>
    /// <param name="SrtPath"> Path to the produced SRT file. </param>
    /// <param name="Lang">    2-letter ISO language code. </param>
    /// <param name="Title">   Source track title if the original had one (e.g. "English [SDH]"),
    ///                        <c>null</c> for untitled tracks. The muxer uses this to label the
    ///                        OCR'd output so "English" and "English [SDH]" tracks stay
    ///                        distinguishable instead of both showing as "OCR (eng)". </param>
    public readonly record struct OcrMuxResult(string SrtPath, string Lang, string? Title);

    private async Task ExtractTextStreamAsync(
        string            inputPath,
        int               streamIndex,
        string            fmt,            // "srt" or "ass"
        string            outPath,
        Func<string,Task> log,
        CancellationToken ct)
    {
        // mov_text cannot be transcoded to ASS; SRT is the safe default for any source codec.
        string codec = fmt == "ass" ? "ass" : "srt";
        string args = $"-y -i \"{inputPath}\" -map 0:{streamIndex} -c:s {codec} \"{outPath}\"";

        await log($"Extracting subtitle stream {streamIndex} → {Path.GetFileName(outPath)}");

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // Drain streams to avoid deadlock on large stderr output.
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync(ct) only cancels the wait — ffmpeg keeps writing to the sidecar
            // and holds the file handle. Kill it and delete the partial output so we don't
            // leave a half-written .srt next to the user's video.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            throw;
        }
        if (proc.ExitCode != 0)
        {
            var err = await stdErrTask;
            var tail = string.Join("\n", err.Split('\n').TakeLast(10));
            throw new Exception($"ffmpeg exit {proc.ExitCode}: {tail}");
        }
    }
}
