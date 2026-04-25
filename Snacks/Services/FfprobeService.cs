using Newtonsoft.Json;
using Snacks.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Snacks.Services;

/// <summary> Wrapper around the <c>ffprobe</c> command-line tool for media file analysis. </summary>
public class FfprobeService
{
    private readonly string _ffprobePath;
    private readonly string _ffmpegPath;
    private static readonly Regex BlackDetectRegex = new(
        @"black_start:(?<start>[\d.]+)\s+black_end:(?<end>[\d.]+)\s+black_duration:(?<dur>[\d.]+)",
        RegexOptions.Compiled);

    /// <summary>
    ///     Initializes the service, resolving the ffprobe binary path from the
    ///     <c>FFPROBE_PATH</c> environment variable, or defaulting to <c>ffprobe</c>.
    /// </summary>
    public FfprobeService()
    {
        _ffprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
        _ffmpegPath  = Environment.GetEnvironmentVariable("FFMPEG_PATH")  ?? "ffmpeg";
    }

    /// <summary>
    ///     Runs ffprobe on the specified file and returns the parsed stream, packet,
    ///     and format metadata. Returns an empty <see cref="ProbeResult"/> on failure.
    /// </summary>
    /// <param name="fileInput">Absolute path to the media file to probe.</param>
    /// <returns>A <see cref="ProbeResult"/> containing all stream and format metadata.</returns>
    public async Task<ProbeResult> ProbeAsync(string fileInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileInput);

        string flags = "-v quiet -print_format json -show_streams -show_format";
        string command = $"{flags} \"{fileInput}\"";

        var processStartInfo = new ProcessStartInfo(_ffprobePath)
        {
            Arguments = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                outputBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        process.WaitForExit(); // Ensures async OutputDataReceived/ErrorDataReceived events have finished firing

        // ffprobe writes JSON to stdout, but some builds redirect it to stderr.
        // Use whichever stream captured more content.
        string correctOutput = outputBuilder.Length > errorBuilder.Length
            ? outputBuilder.ToString()
            : errorBuilder.ToString();

        try
        {
            int jsonIndex = correctOutput.IndexOf('{');
            if (jsonIndex >= 0)
            {
                correctOutput = correctOutput.Substring(jsonIndex);
                return JsonConvert.DeserializeObject<ProbeResult>(correctOutput) ?? new ProbeResult();
            }
        }
        catch
        {
            // Deserialize can throw on malformed JSON — return an empty result rather than propagating.
        }

        return new ProbeResult();
    }

    /// <summary> Returns the FFmpeg <c>-map</c> argument for the first video stream in the probe result. </summary>
    /// <param name="probe">The ffprobe analysis of the source file.</param>
    /// <returns>An FFmpeg stream mapping string, or an empty string if no video stream was found.</returns>
    public string MapVideo(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var videoStream = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        return videoStream != null ? $"-map 0:{videoStream.Index}" : "";
    }

    /// <summary>
    ///     Returns the FFmpeg <c>-map</c> and audio codec arguments for the selected audio streams.
    ///     Filters commentary tracks when a language keep-list is active.
    /// </summary>
    /// <param name="probe">The ffprobe analysis of the source file.</param>
    /// <param name="languagesToKeep">
    ///     2-letter ISO codes to retain. Empty or null keeps every audio stream. Matching accepts
    ///     the track's tag in any of its common forms (2-letter, 3-letter, or English name).
    /// </param>
    /// <param name="audioCodec">
    ///     User-selected codec: <c>"copy"</c>, <c>"aac"</c>, <c>"eac3"</c>, or <c>"opus"</c>.
    ///     "copy" is silently upgraded to AAC re-encode when <paramref name="twoChannels"/> is set
    ///     (you can't downmix a copied stream) or when the container is MP4 (which doesn't carry
    ///     every source format).
    /// </param>
    /// <param name="audioBitrateKbps">Target bitrate for re-encoded audio. Ignored when the effective codec is copy.</param>
    /// <param name="twoChannels">When <c>true</c>, audio is downmixed to 2-channel stereo.</param>
    /// <param name="isMatroska">When <c>false</c> (MP4), copy is disallowed — falls back to AAC re-encode.</param>
    /// <returns>FFmpeg stream mapping and codec arguments for audio, or an empty string if no audio streams exist.</returns>
    public string MapAudio(
        ProbeResult               probe,
        IReadOnlyList<string>?    languagesToKeep,
        string                    audioCodec,
        int                       audioBitrateKbps,
        bool                      twoChannels,
        bool                      isMatroska)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var audioStreams = probe.Streams.Where(s => s.CodecType == "audio").ToList();
        if (!audioStreams.Any()) return "";

        // Resolve the effective codec. "copy" is degraded to AAC when the config
        // asks for something copy can't satisfy (downmix, MP4 container).
        var effectiveCodec = ResolveAudioCodec(audioCodec, twoChannels, isMatroska);
        var codecArgs      = BuildAudioCodecArgs(effectiveCodec, audioBitrateKbps, twoChannels);

        if (languagesToKeep != null && languagesToKeep.Count > 0)
        {
            var filtered = audioStreams
                .Where(s => LanguageMatcher.Matches(s.Tags?.Language, s.Tags?.Title, languagesToKeep)
                    && (s.Tags?.Title == null || !s.Tags.Title.ToLower().Contains("comm")))
                .ToList();

            if (filtered.Any())
            {
                var maps = string.Join(" ", filtered.Select(s => $"-map 0:{s.Index}"));
                return $"{maps} {codecArgs}";
            }
        }

        return $"-map 0:a {codecArgs}";
    }

    /// <summary>
    ///     Resolves the user-selected audio codec to what FFmpeg will actually run.
    ///     Downgrades <c>"copy"</c> to AAC when the rest of the config can't satisfy copy
    ///     (a downmix request or an MP4 container with unsupported source codecs).
    /// </summary>
    private static string ResolveAudioCodec(string requested, bool twoChannels, bool isMatroska)
    {
        var codec = (requested ?? "").Trim().ToLowerInvariant();
        if (codec is not ("copy" or "aac" or "eac3" or "opus")) codec = "aac";

        // Copy can't downmix, and MP4 can't carry every codec — fall back to AAC.
        if (codec == "copy" && (twoChannels || !isMatroska)) codec = "aac";
        return codec;
    }

    /// <summary>
    ///     Builds the <c>-c:a ...</c> flag set for the resolved codec, including
    ///     bitrate/VBR flags and the downmix flag when <paramref name="twoChannels"/>.
    /// </summary>
    private static string BuildAudioCodecArgs(string codec, int bitrateKbps, bool twoChannels)
    {
        var downmix = twoChannels ? " -ac 2" : "";
        var br      = bitrateKbps > 0 ? bitrateKbps : 192;

        return codec switch
        {
            "copy" => "-c:a copy",
            "aac"  => $"-c:a aac  -b:a {br}k{downmix}",
            "eac3" => $"-c:a eac3 -b:a {br}k{downmix}",
            "opus" => $"-c:a libopus -b:a {br}k -vbr on{downmix}",
            _      => $"-c:a aac -b:a {br}k{downmix}",
        };
    }

    /// <summary> Bitmap subtitle codecs that can cause FFmpeg to hang — always excluded. </summary>
    internal static readonly HashSet<string> _bitmapSubCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub"
    };

    /// <summary>
    ///     Returns <see langword="true"/> when the probed video's color-transfer function
    ///     indicates an HDR source — either PQ (smpte2084, HDR10/HDR10+/Dolby Vision) or
    ///     HLG (arib-std-b67). Used to gate tone-map filter insertion.
    /// </summary>
    public static bool IsHdr(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var v = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        var t = v?.ColorTransfer?.ToLowerInvariant();
        return t == "smpte2084" || t == "arib-std-b67";
    }

    /// <summary>
    ///     Returns the FFmpeg <c>-map</c> and subtitle codec arguments for the selected subtitle streams.
    ///     Always drops bitmap subtitle codecs (PGS, VOBSUB, DVB) that can cause encoding failures.
    ///     Returns <c>-sn</c> (no subtitles) for MP4 containers, which don't support text subtitle passthrough.
    /// </summary>
    /// <param name="probe">The ffprobe analysis of the source file.</param>
    /// <param name="languagesToKeep">
    ///     2-letter ISO codes to retain. Empty or null keeps every (non-bitmap) subtitle stream. Matching
    ///     accepts the track's tag in any of its common forms (2-letter, 3-letter, or English name).
    /// </param>
    /// <param name="isMatroska">When <c>false</c>, subtitles are always stripped (<c>-sn</c>).</param>
    /// <returns>FFmpeg subtitle mapping arguments, or <c>-sn</c> to strip all subtitles.</returns>
    public string MapSub(ProbeResult probe, IReadOnlyList<string>? languagesToKeep, bool isMatroska)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!isMatroska)
            return "-sn";

        var subtitleStreams = probe.Streams.Where(s => s.CodecType == "subtitle").ToList();

        // Drop bitmap subtitles (PGS, VOBSUB, DVB) — they cause hangs and encoding failures.
        var keepSubs = subtitleStreams
            .Where(s => !_bitmapSubCodecs.Contains(s.CodecName ?? ""))
            .ToList();

        if (languagesToKeep != null && languagesToKeep.Count > 0)
        {
            keepSubs = keepSubs
                .Where(s => LanguageMatcher.Matches(s.Tags?.Language, s.Tags?.Title, languagesToKeep))
                .ToList();
        }

        if (keepSubs.Any())
        {
            var maps = string.Join(" ", keepSubs.Select(s => $"-map 0:{s.Index}"));
            return $"{maps} -c:s copy";
        }

        return "-sn";
    }

    /// <summary>
    ///     A single subtitle stream selected for sidecar extraction.
    /// </summary>
    /// <param name="StreamIndex"> FFmpeg stream index (e.g. <c>0:3</c> — the <c>3</c> here). </param>
    /// <param name="Lang">        Canonical 2-letter ISO code, or <c>"und"</c> if unresolvable. </param>
    /// <param name="CodecName">   The source codec (e.g. <c>"subrip"</c>, <c>"hdmv_pgs_subtitle"</c>). </param>
    /// <param name="IsBitmap">    When <c>true</c>, the stream is image-based and needs OCR to become text. </param>
    /// <param name="Title">       Source track title (e.g. "English [SDH]"), or <c>null</c> if untitled. </param>
    public sealed record SidecarSpec(int StreamIndex, string Lang, string CodecName, bool IsBitmap, string? Title);

    /// <summary>
    ///     Returns the subtitle streams that should be written as sidecar files, honoring
    ///     the language keep-list and optionally including bitmap streams (for the OCR path).
    /// </summary>
    /// <param name="probe">            Probe of the source file. </param>
    /// <param name="languagesToKeep">  2-letter ISO codes to retain; null/empty keeps all. </param>
    /// <param name="includeBitmaps">   When <c>true</c>, bitmap subs (PGS/VobSub/DVB) are returned for OCR. </param>
    public IReadOnlyList<SidecarSpec> SelectSidecarStreams(
        ProbeResult            probe,
        IReadOnlyList<string>? languagesToKeep,
        bool                   includeBitmaps)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var subs = probe.Streams.Where(s => s.CodecType == "subtitle").ToList();
        var keepByLang = languagesToKeep == null || languagesToKeep.Count == 0
            ? subs
            : subs.Where(s => LanguageMatcher.Matches(s.Tags?.Language, s.Tags?.Title, languagesToKeep)).ToList();

        var result = new List<SidecarSpec>();
        foreach (var s in keepByLang)
        {
            bool isBitmap = _bitmapSubCodecs.Contains(s.CodecName ?? "");
            if (isBitmap && !includeBitmaps) continue;
            result.Add(new SidecarSpec(
                StreamIndex: s.Index,
                Lang:        LanguageMatcher.ToTwoLetter(s.Tags?.Language)
                          ?? LanguageMatcher.InferFromTitle(s.Tags?.Title)
                          ?? "und",
                CodecName:   s.CodecName ?? "",
                IsBitmap:    isBitmap,
                Title:       string.IsNullOrWhiteSpace(s.Tags?.Title) ? null : s.Tags.Title));
        }
        return result;
    }

    /// <summary>
    ///     Validates a transcoded file by comparing input and output durations.
    ///     Allows up to 30 seconds or 1% of total duration as tolerance.
    ///     Returns <c>true</c> if the output duration cannot be read (trusts FFmpeg's exit code).
    /// </summary>
    /// <param name="input">Probe result of the source file.</param>
    /// <param name="output">Probe result of the transcoded output file.</param>
    /// <returns><c>true</c> if the output appears valid; <c>false</c> if durations diverge beyond tolerance.</returns>
    public bool ConvertedSuccessfully(ProbeResult input, ProbeResult output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            double inputDuration = GetVideoDuration(input);
            double outputDuration = GetVideoDuration(output);

            // If we can't read the output duration (common with freshly written MKV),
            // trust that the encode completed since FFmpeg exited successfully.
            if (outputDuration <= 0)
                return true;

            double durationDifference = Math.Abs(inputDuration - outputDuration);

            // Allow up to 30 seconds or 1% of total duration, whichever is greater.
            double tolerance = Math.Max(30, inputDuration * 0.01);
            return durationDifference < tolerance;
        }
        catch
        {
            // Trust FFmpeg's exit code if probe fails.
            return true;
        }
    }

    /// <summary>
    ///     Decides whether an output that failed <see cref="ConvertedSuccessfully"/> should still
    ///     be accepted because the missing tail of the source is just blank/black padding.
    ///     Runs <see cref="AnalyzeTailBlackAsync"/> over the gap between the output's end and
    ///     the source's claimed end and returns true when ≥95% of that range is black.
    /// </summary>
    public async Task<bool> IsTailMostlyBlackAsync(
        string            sourcePath,
        ProbeResult       sourceProbe,
        ProbeResult       outputProbe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceProbe);
        ArgumentNullException.ThrowIfNull(outputProbe);

        double sourceDur = GetVideoDuration(sourceProbe);
        double outputDur = GetVideoDuration(outputProbe);
        if (outputDur <= 0 || sourceDur <= outputDur + 1) return false;

        var (black, tail, ok) = await AnalyzeTailBlackAsync(sourcePath, outputDur, sourceDur, ct);
        if (!ok || tail <= 0) return false;
        return (black / tail) >= 0.95;
    }

    /// <summary>
    ///     Runs ffmpeg with the <c>blackdetect</c> filter over a tail range of the source file
    ///     and reports how much of the range is black. Used to distinguish trailing blank padding
    ///     (output is fine) from real content the encoder dropped (output is truncated).
    /// </summary>
    /// <returns>
    ///     <c>blackSeconds</c> = total reported black duration in the range,
    ///     <c>tailSeconds</c>  = clamped length of the analyzed range,
    ///     <c>ok</c>           = true when ffmpeg ran and stderr was parseable.
    /// </returns>
    public async Task<(double blackSeconds, double tailSeconds, bool ok)> AnalyzeTailBlackAsync(
        string sourcePath, double startSec, double endSec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        // Cap the analyzed window so a wildly inflated container duration cannot stall us
        // decoding hours of blank video.
        const double MaxTailSeconds = 30 * 60;
        double tailSeconds = Math.Min(endSec - startSec, MaxTailSeconds);
        if (tailSeconds <= 0) return (0, 0, false);
        double clampedEnd = startSec + tailSeconds;

        string args =
            $"-v info -nostats -ss {startSec.ToString("0.###", CultureInfo.InvariantCulture)} " +
            $"-to {clampedEnd.ToString("0.###", CultureInfo.InvariantCulture)} " +
            $"-i \"{sourcePath}\" -map 0:v:0 -vf blackdetect=d=0.1:pic_th=0.98 -an -sn -f null -";

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) stderr.AppendLine(e.Data);
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            // Drain stdout to keep the buffer from filling, even though -f null produces ~nothing.
            _ = proc.StandardOutput.ReadToEndAsync(ct);

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            proc.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (0, tailSeconds, false);
        }

        double blackTotal = 0;
        bool sawAnyMatch = false;
        foreach (Match m in BlackDetectRegex.Matches(stderr.ToString()))
        {
            sawAnyMatch = true;
            if (double.TryParse(m.Groups["dur"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                blackTotal += d;
        }

        // No matches can mean either "nothing was black" (a healthy non-match) or "ffmpeg
        // produced no useful output" (inconclusive). Distinguish via exit code: a clean exit
        // with no matches means there were no black intervals — that's a real result.
        bool ok = proc.ExitCode == 0 || sawAnyMatch;
        return (blackTotal, tailSeconds, ok);
    }

    /// <summary>
    ///     Returns the duration of the video stream in seconds.
    ///     Takes the maximum of the format-level and stream-level durations to handle
    ///     files where one value is more accurate than the other.
    /// </summary>
    /// <param name="probe">The ffprobe result to read duration from.</param>
    /// <returns>Duration in seconds, or 0 if no video stream or duration is found.</returns>
    public double GetVideoDuration(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        try
        {
            foreach (var stream in probe.Streams)
            {
                if (stream.CodecType == "video")
                {
                    double formatDuration = DurationStringToSeconds(probe.Format.Duration);
                    double streamDuration = DurationStringToSeconds(stream.Duration);
                    return formatDuration > streamDuration ? formatDuration : streamDuration;
                }
            }
        }
        catch
        {
            // Probe data may be incomplete or missing — 0 signals an unusable duration to callers.
        }

        return 0;
    }

    /// <summary>
    ///     Converts an ffprobe/FFmpeg duration string to total seconds.
    ///     Handles both <c>HH:MM:SS[.ff]</c> format and plain decimal second strings.
    /// </summary>
    /// <param name="input">The duration string to parse.</param>
    /// <returns>Total duration in seconds, or 0 if the input is null, empty, or unparseable.</returns>
    public double DurationStringToSeconds(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return 0;

        try
        {
            // Split only on ':' first to preserve fractional seconds in the last component
            string[] colonParts = input.Split(':');
            if (colonParts.Length >= 3)
            {
                return double.Parse(colonParts[0]) * 3600
                     + double.Parse(colonParts[1]) * 60
                     + double.Parse(colonParts[2]); // "SS.ff" parsed as a decimal
            }
            else
            {
                return double.Parse(input);
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary> Converts a duration in seconds to <c>HH:MM:SS</c> format. </summary>
    /// <param name="input">Duration in seconds.</param>
    /// <returns>Zero-padded duration string in <c>HH:MM:SS</c> format.</returns>
    public string SecondsToDurationString(double input)
    {
        int hours = (int)(input / 3600);
        int minutes = (int)((input % 3600) / 60);
        int seconds = (int)(input % 60);

        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }
}