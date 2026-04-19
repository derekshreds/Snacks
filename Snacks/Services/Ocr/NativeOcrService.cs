using System.Diagnostics;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Snacks.Services.Ocr;

/// <summary>
///     In-process, natively-bundled OCR for bitmap subtitle streams. Tesseract
///     pipeline built on top of format-specific parsers for PGS / VobSub / DVB / XSUB.
/// </summary>
/// <remarks>
///     <para>
///         Pipeline per subtitle stream:
///         <list type="number">
///             <item>FFmpeg copies the raw stream out of the container (no transcoding).</item>
///             <item>A format-specific parser decodes the raw stream into timestamped bitmaps.</item>
///             <item><see cref="ImagePreprocessor"/> normalises each bitmap for Tesseract.</item>
///             <item>Cached <see cref="Engine"/> per language OCRs each bitmap to text.</item>
///             <item><see cref="SrtWriter"/> emits the final <c>.srt</c> file.</item>
///         </list>
///     </para>
/// </remarks>
public sealed class NativeOcrService
{
    private readonly TessdataResolver _tessdataResolver;
    private readonly string           _ffmpegPath;

    // One cached engine per language — Tesseract engine construction is ~100ms and
    // allocates model memory, so keeping them around pays for itself within the first file.
    private readonly Dictionary<string, Engine> _engineCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim              _engineLock  = new(1, 1);

    public NativeOcrService(TessdataResolver tessdataResolver)
    {
        ArgumentNullException.ThrowIfNull(tessdataResolver);
        _tessdataResolver = tessdataResolver;
        _ffmpegPath       = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    }

    /// <summary>
    ///     OCRs a single bitmap subtitle stream into an SRT file. Returns the output
    ///     path on success, <c>null</c> on skip/failure.
    /// </summary>
    /// <param name="sourceFile">  The source media file. </param>
    /// <param name="streamIndex"> FFmpeg stream index of the bitmap track. </param>
    /// <param name="lang">        Two-letter ISO code used to pick a Tesseract language pack. </param>
    /// <param name="codecName">   Source codec (e.g. <c>hdmv_pgs_subtitle</c>, <c>dvd_subtitle</c>). </param>
    /// <param name="outputPath">  Target <c>.srt</c> path. </param>
    public async Task<string?> ConvertBitmapToSrtAsync(
        string            sourceFile,
        int               streamIndex,
        string            lang,
        string            codecName,
        string            outputPath,
        Func<string,Task> log,
        CancellationToken ct)
    {
        var tmpDir = Path.Combine(
            Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work"),
            "tmp",
            "ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            await log($"OCR (stream {streamIndex}, {lang}, {codecName}) → {Path.GetFileName(outputPath)}");

            // Copy the subtitle stream out of the container into its native format.
            var (rawPath, idxPath) = await ExtractRawStreamAsync(sourceFile, streamIndex, codecName, tmpDir, log, ct);
            if (rawPath == null || !File.Exists(rawPath))
            {
                await log("OCR: ffmpeg failed to extract subtitle stream — skipping.");
                return null;
            }

            // Pick a parser and decode the raw stream into timestamped bitmaps.
            IAsyncEnumerable<BitmapEvent> events;
            try
            {
                events = PickParser(codecName, rawPath, idxPath, ct);
            }
            catch (NotSupportedException nse)
            {
                await log($"OCR: {nse.Message}");
                return null;
            }

            // Get a Tesseract engine for the target language.
            var engine = await GetEngineAsync(lang, log, ct);
            if (engine == null)
            {
                await log($"OCR: failed to load Tesseract engine for '{lang}' — skipping.");
                return null;
            }

            // Stream bitmaps through Tesseract and into the SRT writer.
            var srt = new SrtWriter();
            int cueCount  = 0;
            int emptyRuns = 0;

            await foreach (var ev in events.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                string text;
                try
                {
                    using var pix  = TesseractOCR.Pix.Image.LoadFromMemory(ev.PngBytes);
                    using var page = engine.Process(pix);
                    text = page.Text ?? "";
                }
                catch (Exception ex)
                {
                    await log($"OCR: Tesseract failed on a cue ({ex.Message}) — skipping cue.");
                    continue;
                }

                srt.Add(ev.Start, ev.End, text);
                cueCount++;
                if (string.IsNullOrWhiteSpace(text)) emptyRuns++;

                if (cueCount % 50 == 0) await log($"OCR: {cueCount} cues processed...");
            }

            if (srt.CueCount == 0)
            {
                await log("OCR: no text recognised — output skipped.");
                return null;
            }

            await srt.WriteAsync(outputPath, ct);
            await log($"OCR: wrote {srt.CueCount} cue(s) to {Path.GetFileName(outputPath)}" +
                      (emptyRuns > 0 ? $" ({emptyRuns} blank OCR result(s) dropped)" : ""));

            return File.Exists(outputPath) ? outputPath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await log($"OCR failed for stream {streamIndex}: {ex.Message}");
            return null;
        }
        finally
        {
            TryDeleteDirectory(tmpDir);
        }
    }

    private static IAsyncEnumerable<BitmapEvent> PickParser(string codecName, string rawPath, string? idxPath, CancellationToken ct)
    {
        // DVB and XSUB are normalised to VobSub during extraction (see ExtractRawStreamAsync),
        // so they flow through the same parser as native DVD subs.
        return codecName.ToLowerInvariant() switch
        {
            "hdmv_pgs_subtitle" or "pgssub" => Parsers.PgsParser.ParseAsync(rawPath, ct),
            "dvd_subtitle" or "dvdsub"
                or "dvb_subtitle" or "dvbsub"
                or "xsub"                   => Parsers.VobSubParser.ParseAsync(
                                                   idxPath ?? throw new InvalidOperationException("VobSub-compatible parse needs an idx file"),
                                                   rawPath, ct),
            _ => throw new NotSupportedException($"Native OCR does not support codec '{codecName}'."),
        };
    }

    /// <summary>
    ///     Produces the raw bitstream the parser needs. PGS streams are copied verbatim from
    ///     the container. VobSub / DVB / XSUB are all routed through FFmpeg's <c>dvdsub</c>
    ///     encoder, which writes an <c>.idx</c>/<c>.sub</c> pair our VobSub parser can consume —
    ///     this side-steps needing to write a separate DVB decoder and gives us immediate support
    ///     for any bitmap format FFmpeg can decode.
    /// </summary>
    private async Task<(string? raw, string? idx)> ExtractRawStreamAsync(
        string sourceFile, int streamIndex, string codecName, string tmpDir,
        Func<string,Task> log, CancellationToken ct)
    {
        bool isPgs = codecName.Equals("hdmv_pgs_subtitle", StringComparison.OrdinalIgnoreCase)
                  || codecName.Equals("pgssub",            StringComparison.OrdinalIgnoreCase);

        bool isVobSubFamily = !isPgs;  // dvd_subtitle / dvb_subtitle / xsub all take the vobsub path

        string args;
        string outPath;

        if (isPgs)
        {
            outPath = Path.Combine(tmpDir, "stream.sup");
            args = $"-y -i \"{sourceFile}\" -map 0:{streamIndex} -c:s copy \"{outPath}\"";
        }
        else
        {
            // For native VobSub we could `-c:s copy` into a .idx, but FFmpeg happily transcodes
            // any bitmap subtitle codec to dvdsub and writes the .idx+.sub pair in one shot —
            // same pipeline for DVD, DVB, and XSUB.
            outPath = Path.Combine(tmpDir, "stream.idx");
            args = $"-y -i \"{sourceFile}\" -map 0:{streamIndex} -c:s dvdsub \"{outPath}\"";
        }

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
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await stdErrTask;
            var tail = string.Join(" ", err.Split('\n').TakeLast(5));
            await log($"ffmpeg stream copy exit {proc.ExitCode}: {tail}");
            return (null, null);
        }

        if (isVobSubFamily)
        {
            // ffmpeg writes {tmpDir}/stream.idx and {tmpDir}/stream.sub
            string subPath = Path.ChangeExtension(outPath, ".sub");
            if (!File.Exists(subPath))
            {
                await log("ffmpeg wrote .idx but no .sub — VobSub-family extraction failed.");
                return (null, null);
            }
            return (subPath, outPath);
        }

        return (outPath, null);
    }

    private async Task<Engine?> GetEngineAsync(string lang, Func<string,Task> log, CancellationToken ct)
    {
        var tessLang = TessdataResolver.MapToTesseractLang(lang);

        await _engineLock.WaitAsync(ct);
        try
        {
            if (_engineCache.TryGetValue(tessLang, out var existing)) return existing;

            var tessdataDir = await _tessdataResolver.ResolveAsync(tessLang, log, ct);

            try
            {
                // 'Default' engine mode uses the LSTM model when available and falls back to
                // the legacy engine otherwise — the safe choice across tessdata variants.
                var engine = new Engine(tessdataDir, tessLang, EngineMode.Default);
                _engineCache[tessLang] = engine;
                return engine;
            }
            catch (Exception ex)
            {
                await log($"OCR: failed to construct Tesseract engine for '{tessLang}': {ex.Message}");
                return null;
            }
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup; don't mask the real exception */ }
    }
}
