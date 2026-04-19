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
        foreach (var spec in specs)
        {
            langCounts.TryGetValue(spec.Lang, out int seen);
            langCounts[spec.Lang] = seen + 1;
            string suffix  = seen == 0 ? "" : $".{seen + 1}";

            // OCR output is always .srt — Subtitle Edit can't produce styled ASS.
            string outFmt  = spec.IsBitmap ? "srt" : fmt;
            string outPath = $"{sidecarBase}.{spec.Lang}{suffix}.{outFmt}";

            try
            {
                if (spec.IsBitmap)
                {
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await log($"Sidecar extraction failed for stream {spec.StreamIndex} ({spec.Lang}): {ex.Message}");
            }
        }

        await log($"Sidecar extraction: wrote {written.Count} file(s).");
        return written;
    }

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

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await stdErrTask;
            var tail = string.Join("\n", err.Split('\n').TakeLast(10));
            throw new Exception($"ffmpeg exit {proc.ExitCode}: {tail}");
        }
    }
}
