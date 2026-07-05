using System.Diagnostics;
using System.Globalization;

namespace Snacks.Services;

/// <summary>
///     Deep file-integrity verification for the library-health page. Where the
///     health report flags files from cheap DB-cached scan data, this service
///     answers "is this file actually decodable?" by running ffmpeg decode
///     samples (<c>-v error … -f null -</c>) at the start, middle, and end of
///     the file — the same trick Tdarr-style health checks use, but bounded to
///     a few seconds of decoding per sample instead of a full-length pass.
/// </summary>
public sealed class FileHealthService
{
    private readonly FfprobeService _ffprobeService;
    private readonly string _ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";

    /// <summary>
    ///     At most two concurrent verifications — each one is a real ffmpeg decode,
    ///     and the page exposes a per-row button a user can hammer.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(2, 2);

    /// <summary> Seconds of content decoded per sample point. </summary>
    private const int SampleSeconds = 8;

    /// <summary> Hard cap per ffmpeg invocation, generous for slow NAS reads of 4K content. </summary>
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(90);

    public FileHealthService(FfprobeService ffprobeService)
    {
        ArgumentNullException.ThrowIfNull(ffprobeService);
        _ffprobeService = ffprobeService;
    }

    /// <summary> Outcome of a verification run. </summary>
    public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Issues);

    /// <summary>
    ///     stderr fragments that are artifacts of the sampling method, not real defects.
    ///     We input-seek (<c>-ss</c> before <c>-i</c>) into the middle/end of the file and
    ///     feed decoded frames to the null muxer; seeking into an open-GOP HEVC stream
    ///     hands the muxer a few frames whose DTS aren't strictly increasing, so ffmpeg
    ///     prints "non monotonically increasing dts to muxer". The frames DID decode —
    ///     which is exactly what this check verifies — so it's a muxer timestamp complaint,
    ///     not a corruption signal. Files that trip it play back fine; counting it as a
    ///     failure produced false "VERIFY FAILED" reports across healthy HEVC libraries.
    ///     Genuine corruption surfaces as distinct lines ("Invalid data found", "error
    ///     while decoding", "corrupt", …) which are NOT matched here.
    /// </summary>
    private static readonly string[] _benignVerifyNoise =
    {
        "non monotonically increasing dts to muxer",
    };

    /// <summary> True when a stderr line is sampling-method noise rather than a decode defect. </summary>
    internal static bool IsBenignVerifyNoise(string line) =>
        _benignVerifyNoise.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Decodes short samples at three offsets and collects every decoder error.
    ///     A clean file returns <c>Ok = true</c> with no issues; a damaged one returns
    ///     the (deduplicated, truncated) ffmpeg error lines so the user can see what's
    ///     wrong without reading logs.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return new VerifyResult(false, new[] { "File no longer exists on disk." });

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var probe    = await _ffprobeService.ProbeAsync(filePath, cancellationToken);
            var issues   = new List<string>();
            double duration = 0;
            if (probe.Format?.Duration != null)
                double.TryParse(probe.Format.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

            if (probe.Streams.Length == 0)
                issues.Add("ffprobe found no streams — the container is unreadable.");
            else
            {
                bool hasVideo = probe.Streams.Any(s => s.CodecType == "video");
                bool hasAudio = probe.Streams.Any(s => s.CodecType == "audio");
                if (!hasVideo && !hasAudio) issues.Add("No video or audio streams in the container.");
                else if (!hasAudio)         issues.Add("No audio streams.");
                if (duration <= 0)          issues.Add("Container reports no duration (often a truncated file).");
            }

            // Sample offsets: start, middle, and near the end (truncation shows up
            // there first). Collapse to a single pass for very short files.
            var offsets = duration > SampleSeconds * 4
                ? new[] { 0.0, duration / 2, Math.Max(0, duration - SampleSeconds * 2) }
                : new[] { 0.0 };

            foreach (var offset in offsets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleErrors = await DecodeSampleAsync(filePath, offset, cancellationToken);
                foreach (var line in sampleErrors)
                {
                    var tagged = offset == 0 ? line : $"[{TimeSpan.FromSeconds(offset):hh\\:mm\\:ss}] {line}";
                    if (!issues.Contains(tagged)) issues.Add(tagged);
                }
                if (issues.Count >= 12) break; // enough signal — don't flood the UI
            }

            return new VerifyResult(issues.Count == 0, issues);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary> Runs one bounded decode sample; returns deduplicated stderr error lines. </summary>
    private async Task<List<string>> DecodeSampleAsync(string filePath, double offsetSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        // -v error keeps stderr to genuine decode problems; -f null discards output.
        // ArgumentList so hostile filenames can't inject options.
        foreach (var flag in new[]
                 {
                     "-v", "error", "-nostdin",
                     "-ss", offsetSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                     "-i", filePath,
                     "-t", SampleSeconds.ToString(CultureInfo.InvariantCulture),
                     "-map", "0:v:0?", "-map", "0:a?",
                     "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                 })
            psi.ArgumentList.Add(flag);

        using var process = new Process { StartInfo = psi };
        var errors = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                // Keep lines compact and deduplicated — decoder errors repeat per frame.
                // Benign muxer-timestamp noise is collected here (so the exit-code fallback
                // can tell "printed nothing" from "printed only noise") and filtered out of
                // the returned issues below.
                var line = e.Data.Length > 300 ? e.Data[..300] + "…" : e.Data;
                if (errors.Count < 50 && !errors.Contains(line)) errors.Add(line);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(SampleTimeout);
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            // Return a snapshot — the killed process's stderr reader can still
            // flush buffered lines into `errors` after we return, and the caller
            // enumerates the list without taking the lock.
            lock (errors)
            {
                var timedOut = errors.Where(l => !IsBenignVerifyNoise(l)).ToList();
                timedOut.Add($"Decode sample timed out after {SampleTimeout.TotalSeconds:0}s — likely a hang-inducing corruption.");
                return timedOut;
            }
        }
        process.WaitForExit(); // drain async stderr events

        lock (errors)
        {
            // Drop benign muxer-timestamp noise; a sample whose ONLY output was that noise
            // is a healthy decode. Gate the exit-code fallback on the RAW output so a
            // non-zero exit accompanied only by benign noise doesn't resurface as a failure.
            var genuine = errors.Where(l => !IsBenignVerifyNoise(l)).ToList();
            if (process.ExitCode != 0 && errors.Count == 0)
                genuine.Add($"ffmpeg exited with code {process.ExitCode} but printed no error.");
            return genuine;
        }
    }
}
