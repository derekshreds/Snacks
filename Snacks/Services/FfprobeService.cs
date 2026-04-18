using Newtonsoft.Json;
using Snacks.Models;
using System.Diagnostics;

namespace Snacks.Services;

/// <summary> Wrapper around the <c>ffprobe</c> command-line tool for media file analysis. </summary>
public class FfprobeService
{
    private readonly string _ffprobePath;

    /// <summary>
    ///     Initializes the service, resolving the ffprobe binary path from the
    ///     <c>FFPROBE_PATH</c> environment variable, or defaulting to <c>ffprobe</c>.
    /// </summary>
    public FfprobeService()
    {
        _ffprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
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
                .Where(s => LanguageMatcher.Matches(s.Tags?.Language, languagesToKeep)
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
    private static readonly HashSet<string> _bitmapSubCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub"
    };

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
            var filtered = keepSubs
                .Where(s => LanguageMatcher.Matches(s.Tags?.Language, languagesToKeep))
                .ToList();
            if (filtered.Any())
                keepSubs = filtered;
        }

        if (keepSubs.Any())
        {
            var maps = string.Join(" ", keepSubs.Select(s => $"-map 0:{s.Index}"));
            return $"{maps} -c:s copy";
        }

        return "-sn";
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