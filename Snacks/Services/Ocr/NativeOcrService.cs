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
    private readonly Dictionary<string, Engine>                _engineCache       = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubtitleSpellChecker>  _spellCheckerCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim                             _engineLock        = new(1, 1);

    // Process-wide OCR slot. Multi-slot encoding can run several jobs in parallel
    // on a single node, but Tesseract's engine state isn't safe to drive
    // concurrently and the OCR pipeline runs one cue at a time anyway. Serializing
    // at the movie level (caller holds the slot for the full pass) avoids cross-job
    // engine corruption and keeps log output coherent.
    private readonly SemaphoreSlim _ocrSlot = new(1, 1);
    private string?                _ocrSlotHolder;

    public NativeOcrService(TessdataResolver tessdataResolver)
    {
        ArgumentNullException.ThrowIfNull(tessdataResolver);
        _tessdataResolver = tessdataResolver;
        _ffmpegPath       = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    }

    /// <summary>
    ///     Acquires the node-wide OCR slot. The returned <see cref="IDisposable"/>
    ///     releases it when disposed. Callers should hold it across all bitmap
    ///     OCR work for a single movie so a parallel encode's OCR pass waits its
    ///     turn instead of racing on the cached Tesseract engines.
    /// </summary>
    /// <param name="holderLabel"> Friendly label (typically the file name) used in
    ///     the "waiting for OCR" log line on the next caller. </param>
    /// <param name="log"> Async logger; receives a "queued behind {current}" line
    ///     when the slot is contended. </param>
    public async Task<IDisposable> AcquireOcrSlotAsync(
        string holderLabel, Func<string,Task> log, CancellationToken ct)
    {
        if (_ocrSlot.CurrentCount == 0)
            await log($"OCR: waiting for node OCR slot — currently busy with '{_ocrSlotHolder ?? "another job"}'");

        await _ocrSlot.WaitAsync(ct);
        _ocrSlotHolder = holderLabel;
        return new OcrSlotReleaser(this);
    }

    private sealed class OcrSlotReleaser : IDisposable
    {
        private readonly NativeOcrService _svc;
        private bool _disposed;
        public OcrSlotReleaser(NativeOcrService svc) { _svc = svc; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _svc._ocrSlotHolder = null;
            _svc._ocrSlot.Release();
        }
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

            // Buffer the whole parser output into memory so we can run a file-wide skew
            // detection pass before OCR. Cue bitmaps are small (typically <50 KB each)
            // and a feature-length film has <2000 cues, so a few dozen MB at peak.
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

            var buffered = new List<BitmapEvent>();
            await foreach (var ev in events.WithCancellation(ct)) buffered.Add(ev);
            if (buffered.Count == 0)
            {
                await log("OCR: parser produced no cues.");
                return null;
            }

            // Skew detection runs per-cue. Real-world subtitle tracks often mix italic
            // narration with upright dialogue, so a single file-wide angle breaks the
            // upright lines. Per-cue detection sees fewer strokes but the threshold is
            // tuned strict enough to only fire when a cue is clearly italic.
            bool latinScript = IsLatinScript(TessdataResolver.MapToTesseractLang(lang));

            // Get a Tesseract engine for the target language.
            var engine = await GetEngineAsync(lang, log, ct);
            if (engine == null)
            {
                await log($"OCR: failed to load Tesseract engine for '{lang}' — skipping.");
                return null;
            }

            // Spellcheck / bigram post-pass temporarily disabled to evaluate raw
            // Tesseract output. Re-enable by uncommenting the GetSpellChecker call here
            // and the spellChecker.Correct(text) line below.
            // var spellChecker = GetSpellChecker(lang);

            // OCR each cue, detecting slant per-cue so mixed italic/upright content is handled.
            var srt = new SrtWriter();
            int cueCount  = 0;
            int emptyRuns = 0;
            int deshearedCount = 0;

            for (int idx = 0; idx < buffered.Count; idx++)
            {
                var ev = buffered[idx];
                ct.ThrowIfCancellationRequested();

                // Per-cue skew detection (only for Latin-script languages). Returns 0 for
                // upright cues, which is the common case — upright lines don't get touched.
                double cueSkewRad = latinScript
                    ? ImagePreprocessor.DetectPerCueSkew(ev.Rgba, ev.Width, ev.Height)
                    : 0;
                if (cueSkewRad != 0) deshearedCount++;

                byte[] png;
                try
                {
                    // 6× brings 40-60px PGS glyphs to roughly 300 DPI, which is Tesseract's
                    // trained input scale. Going lower costs accuracy; going higher adds cost
                    // without a material quality gain.
                    png = ImagePreprocessor.Preprocess(ev.Rgba, ev.Width, ev.Height, upscale: 6, skewRad: cueSkewRad);
                }
                catch (Exception ex)
                {
                    await log($"OCR: preprocessing failed on cue {idx} ({ex.Message}) — skipping.");
                    continue;
                }

                string text;
                try
                {
                    using var pix = TesseractOCR.Pix.Image.LoadFromMemory(png);
                    // Tell Tesseract the preprocessed image is at ~300 DPI. Without this
                    // its internal scaling heuristics use a 70 DPI default and systematically
                    // misjudge font size, costing 10-20% word accuracy.
                    pix.XRes = 300;
                    pix.YRes = 300;
                    using var page = engine.Process(pix, TesseractOCR.Enums.PageSegMode.SingleBlock);
                    text = page.Text ?? "";
                    // Deterministic OCR character fixes only (pipe/bracket → I,
                    // line-start = → -). Full dictionary spellcheck remains disabled.
                    text = SubtitleSpellChecker.ApplyDeterministicSubs(text);
                }
                catch (Exception ex)
                {
                    await log($"OCR: Tesseract failed on cue {idx} ({ex.Message}) — skipping.");
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
                      (emptyRuns      > 0 ? $" ({emptyRuns} blank OCR result(s) dropped)" : "") +
                      (deshearedCount > 0 ? $"; {deshearedCount}/{buffered.Count} cues desheared" : ""));

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
    ///     Tesseract language codes whose writing systems use the Latin alphabet (plus
    ///     its diacritic variants). Only these langs opt in to italic-deshear preprocessing —
    ///     italic typography is Latin-script convention and the detector gives false
    ///     positives on cursive Arabic, stroke-varied CJK, etc.
    /// </summary>
    private static readonly HashSet<string> _latinScriptLangs = new(StringComparer.Ordinal)
    {
        "eng", "spa", "fra", "deu", "ita", "por", "nld",
        "swe", "nor", "dan", "fin", "pol", "tur", "ces",
        "hun", "vie", "ind",
    };

    private static bool IsLatinScript(string tessLang) => _latinScriptLangs.Contains(tessLang);

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

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync(ct) only cancels the wait — the child process keeps running
            // and holds file handles on the tmp output, which breaks our tmpDir cleanup. Kill it.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

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
                // tessdata_best ships LSTM-only models; EngineMode.Default tries to load the
                // legacy engine too and can yield worse results than pure LSTM. Match the
                // tessdata variant we're actually using.
                var engine = new Engine(tessdataDir, tessLang, EngineMode.LstmOnly);

                // Bias the recogniser toward dictionary words. LSTM's default non-dict penalty
                // (~0.1) lets plausible-but-wrong readings like "heiped" win over "helped";
                // raising it to 0.25 flips the ranking without being so aggressive that rare
                // but legitimate words get rewritten.
                try
                {
                    engine.SetVariable("language_model_penalty_non_dict_word",      "0.25");
                    engine.SetVariable("language_model_penalty_non_freq_dict_word", "0.25");
                    engine.SetVariable("tessedit_enable_dict_correction",           "1");
                }
                catch (Exception ex)
                {
                    await log($"OCR: Tesseract variable tuning partially failed ({ex.Message}) — continuing with defaults.");
                }

                _engineCache[tessLang] = engine;
                return engine;
            }
            catch (Exception ex)
            {
                // TesseractOCR wraps native-load failures in TargetInvocationException;
                // the outer message is generic, so unwrap to surface the real cause
                // (DllNotFoundException / missing tessdata file / etc).
                var root = ex;
                while (root.InnerException != null) root = root.InnerException;
                await log($"OCR: failed to construct Tesseract engine for '{tessLang}': {root.GetType().Name}: {root.Message}");
                return null;
            }
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private SubtitleSpellChecker GetSpellChecker(string lang)
    {
        var tessLang = TessdataResolver.MapToTesseractLang(lang);
        if (_spellCheckerCache.TryGetValue(tessLang, out var cached)) return cached;
        // Load once per language and cache. LoadFor always returns an instance —
        // an empty dictionary just means the deterministic pass runs and edit-1 is skipped.
        var checker = SubtitleSpellChecker.LoadFor(tessLang);
        _spellCheckerCache[tessLang] = checker;
        return checker;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup; don't mask the real exception */ }
    }
}
