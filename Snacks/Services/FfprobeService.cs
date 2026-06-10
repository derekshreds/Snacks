using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Snacks.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stream = Snacks.Models.Stream;

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

        // ArgumentList (not a quoted Arguments string) so filenames containing
        // double quotes can't split the argument or inject extra ffprobe options.
        var processStartInfo = new ProcessStartInfo(_ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var flag in new[] { "-v", "quiet", "-print_format", "json", "-show_streams", "-show_format" })
            processStartInfo.ArgumentList.Add(flag);
        processStartInfo.ArgumentList.Add(fileInput);

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

        // Hard watchdog on top of the caller's token: ffprobe is known to hang on
        // damaged containers, and several callers (notably the auto-scan loop) pass
        // tokens that are never cancelled. A hung probe used to hold the scan lock
        // forever — every future scan tick silently no-oped until restart.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(watchdog.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            // Only propagate cancellation the caller asked for. A watchdog kill
            // must THROW, not return an empty ProbeResult — an empty result is
            // indistinguishable from a real probe of a broken file, and callers
            // like the auto-scan companion validator would treat duration=0 as
            // "encode didn't match the original" and delete valid outputs.
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"ffprobe timed out after 5 minutes: {fileInput}");
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
    ///     Per-codec spec used by the audio planner. Encapsulates the FFmpeg encoder name,
    ///     bitrate emission style, channel ceiling, and container compatibility for each
    ///     supported output codec.
    /// </summary>
    private sealed record AudioCodecSpec(
        string Encoder,
        AudioBitrateStyle BitrateStyle,
        int    DefaultBitrateKbps,
        int    MaxChannels,
        bool   AllowedInMp4,
        bool   AllowedInWebm);

    private enum AudioBitrateStyle { Cbr, OpusVbr, None }

    /// <summary>
    ///     Codec-name → spec table consulted by the planner. Keys are lower-case logical names
    ///     (the same strings the UI dropdown emits). Adding a new codec is a single-row change here
    ///     plus a UI option.
    /// </summary>
    private static readonly Dictionary<string, AudioCodecSpec> _codecSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aac"]  = new("aac",      AudioBitrateStyle.Cbr,     192, 8, AllowedInMp4: true,  AllowedInWebm: false),
        ["ac3"]  = new("ac3",      AudioBitrateStyle.Cbr,     448, 6, AllowedInMp4: true,  AllowedInWebm: false),
        ["eac3"] = new("eac3",     AudioBitrateStyle.Cbr,     384, 8, AllowedInMp4: true,  AllowedInWebm: false),
        ["opus"] = new("libopus",  AudioBitrateStyle.OpusVbr, 192, 8, AllowedInMp4: false, AllowedInWebm: true),
    };

    /// <summary>
    ///     FFmpeg-channel-count for each layout name in
    ///     <see cref="AudioOutputProfile.Layout"/>. <c>null</c> means "keep source layout"
    ///     (no <c>-ac</c> flag is emitted).
    /// </summary>
    private static int? LayoutToChannels(string layout) => layout?.Trim().ToLowerInvariant() switch
    {
        "mono"   => 1,
        "stereo" => 2,
        "5.1"    => 6,
        "7.1"    => 8,
        _        => null, // "Source" or unrecognized → no -ac flag
    };

    /// <summary>
    ///     Quality ranking for source codec tie-breaking when multiple sources can satisfy
    ///     a re-encode target. Higher = preferred. Lossless sources rank above lossy sources
    ///     so a 5.1 TrueHD beats a 5.1 AC3 as the encode source.
    /// </summary>
    private static int SourceCodecQuality(string? codec) => (codec ?? "").ToLowerInvariant() switch
    {
        "flac" or "truehd" or "mlp" or "alac" or "pcm_s16le" or "pcm_s24le" => 4,
        "dts" or "dtshd"                                                    => 3,
        "eac3"                                                              => 2,
        "ac3"                                                               => 2,
        "aac" or "opus" or "vorbis" or "mp3"                                => 1,
        _                                                                   => 0,
    };

    /// <summary>
    ///     Plans the audio portion of an FFmpeg command from the user's
    ///     <see cref="EncoderOptions.PreserveOriginalAudio"/> + <see cref="EncoderOptions.AudioOutputs"/>
    ///     configuration. For each kept language, every output profile produces either a
    ///     pass-through (when a source track already matches codec+layout) or a re-encode from
    ///     the best matching source track. Never reduces a kept language to zero output streams —
    ///     if no requested output can be satisfied, the highest-channel source is force-copied
    ///     so the language survives.
    /// </summary>
    /// <param name="probe">The ffprobe analysis of the source file.</param>
    /// <param name="languagesToKeep">
    ///     2-letter ISO codes to retain. Empty or null keeps every audio stream. Matches via
    ///     <see cref="LanguageMatcher.Matches"/> (handles 2/3-letter and English name forms).
    /// </param>
    /// <param name="preserveOriginalAudio">
    ///     When <see langword="true"/>, every kept source track is copied through alongside
    ///     any encoded variants in <paramref name="audioOutputs"/>.
    /// </param>
    /// <param name="audioOutputs">Encoded variants to emit per kept language, or <see langword="null"/>/empty for none.</param>
    /// <param name="container">
    ///     Output container token ("mkv", "mp4", or "webm"). MP4 forces non-MP4-safe codecs to AAC and
    ///     re-encodes copy sources the container can't carry. WebM forces non-WebM-safe codecs to Opus
    ///     and only stream-copies Opus / Vorbis sources. Matroska is permissive.
    /// </param>
    /// <param name="warnings">Receives one entry per fallback/clamp/skipped-profile event so callers can surface them in the per-job log.</param>
    /// <param name="autoSetDefault">
    ///     When <c>true</c>, flags the first kept audio output as the default track and
    ///     clears the default flag on the others. The reorder is implicit — buckets are
    ///     already iterated in keep-list order, so the first output belongs to the
    ///     top-priority language.
    /// </param>
    /// <returns>FFmpeg map + per-output-stream codec arguments. Empty string when the source has no audio streams.</returns>
    public string MapAudio(
        ProbeResult                       probe,
        IReadOnlyList<string>?            languagesToKeep,
        bool                              preserveOriginalAudio,
        IReadOnlyList<AudioOutputProfile>? audioOutputs,
        string                            container,
        out List<string>                  warnings,
        bool                              autoSetDefault = false)
    {
        ArgumentNullException.ThrowIfNull(probe);
        warnings = new List<string>();

        var audioStreams = probe.Streams.Where(s => s.CodecType == "audio").ToList();
        if (audioStreams.Count == 0) return "";

        // Group source tracks by language bucket. A language bucket is either one of the
        // user's keep-codes (matched via LanguageMatcher) or — when no keep-list is set —
        // one bucket per distinct language tag present on the source. Commentary tracks
        // are dropped at the source level so they never seed an encode target.
        var keepList = languagesToKeep is { Count: > 0 } ? languagesToKeep : null;
        var buckets  = new List<(string Bucket, List<Stream> Sources)>();

        bool IsCommentary(Stream s) => IsCommentaryTitle(s.Tags?.Title);

        if (keepList != null)
        {
            foreach (var lang in keepList)
            {
                var sources = audioStreams
                    .Where(s => LanguageMatcher.Matches(s.Tags?.Language, s.Tags?.Title, new[] { lang })
                             && !IsCommentary(s))
                    .ToList();
                if (sources.Count > 0) buckets.Add((lang, sources));
            }
        }
        else
        {
            // No keep-list: bucket by the source's own language tag (or "und" for unknowns)
            // so we still apply per-language fan-out + safeguards.
            foreach (var grp in audioStreams
                .Where(s => !IsCommentary(s))
                .GroupBy(s => (s.Tags?.Language ?? "und").ToLowerInvariant()))
            {
                buckets.Add((grp.Key, grp.ToList()));
            }
        }

        if (buckets.Count == 0) return "";

        var profiles = (audioOutputs ?? Array.Empty<AudioOutputProfile>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Codec))
            .ToList();

        var maps         = new StringBuilder();   // -map 0:N tokens
        var codecArgs    = new StringBuilder();   // -c:a:N / -b:a:N / -ac:a:N tokens
        var meta         = new StringBuilder();   // -metadata:s:a:N language=... / title=... tokens
        int outIndex     = 0;                     // running output-audio-stream index

        foreach (var (bucket, sources) in buckets)
        {
            // Plan first, emit second. The plan phase classifies each profile as either a
            // dedup-to-copy (against an existing source track) or a re-encode, then the emit
            // phase writes copies before re-encodes so output stream order matches user
            // expectation ("the original is the default").
            var copySourceIndices = new HashSet<int>();
            var reencodes         = new List<(int srcIndex, string codec, int channels, int bitrateKbps)>();

            foreach (var profile in profiles)
            {
                int? targetCh = LayoutToChannels(profile.Layout);

                // Dedup-to-copy: a source track whose codec+channels already match this output?
                Stream? exactMatch = sources
                    .Where(s => string.Equals(s.CodecName, profile.Codec, StringComparison.OrdinalIgnoreCase)
                             && (targetCh == null || s.Channels == targetCh.Value))
                    .OrderByDescending(s => s.Channels)
                    .FirstOrDefault();

                if (exactMatch != null)
                {
                    copySourceIndices.Add(exactMatch.Index);
                    continue;
                }

                // No exact match — pick the best source to re-encode from. Sources must have
                // at least the requested channel count (no upmix); prefer highest channels,
                // then highest-quality source codec.
                var minCh = targetCh ?? 0;
                var candidate = sources
                    .Where(s => s.Channels >= minCh)
                    .OrderByDescending(s => s.Channels)
                    .ThenByDescending(s => SourceCodecQuality(s.CodecName))
                    .FirstOrDefault();

                if (candidate == null)
                {
                    warnings.Add(
                        $"Audio: profile {profile.Codec} {profile.Layout} skipped for language '{bucket}' — " +
                        $"no source track with {minCh}+ channels.");
                    continue;
                }

                int encodeCh = targetCh ?? candidate.Channels;
                var resolved = ResolveAudioCodec(profile.Codec, encodeCh, container, warnings);
                reencodes.Add((candidate.Index, resolved.codec, resolved.channels, profile.BitrateKbps));
            }

            // PreserveOriginal: every kept source not already covered by a dedup is copied.
            if (preserveOriginalAudio)
            {
                foreach (var src in sources) copySourceIndices.Add(src.Index);
            }

            // Empty-language safeguard: never let a kept language vanish entirely.
            if (copySourceIndices.Count == 0 && reencodes.Count == 0)
            {
                var fallback = sources.OrderByDescending(s => s.Channels).First();
                warnings.Add(
                    $"Audio: language '{bucket}' had no satisfiable output profiles — passing source track #{fallback.Index} through unchanged.");
                copySourceIndices.Add(fallback.Index);
            }

            // -- Emit copies first (in source-index order). ----------------------------
            foreach (var srcIndex in copySourceIndices.OrderBy(i => i))
            {
                var src = sources.First(s => s.Index == srcIndex);

                if (!ContainerCanCopySource(src.CodecName, container))
                {
                    string reencTarget = IsWebm(container) ? "opus" : "aac";
                    warnings.Add(
                        $"Audio: source track #{src.Index} ({src.CodecName}) cannot be copied to {container.ToUpperInvariant()} — re-encoding to {reencTarget.ToUpperInvariant()}.");
                    var fallback = ResolveAudioCodec(reencTarget, src.Channels, container, warnings);
                    maps.Append($"-map 0:{src.Index} ");
                    codecArgs.Append(BuildAudioCodecArgs(fallback.codec, fallback.channels, 0, outIndex)).Append(' ');
                    AppendAudioMeta(meta, outIndex, bucket, fallback.codec, fallback.channels);
                }
                else
                {
                    // -c:a copy preserves source language + title metadata, so we leave
                    // those alone here — overriding would clobber descriptive titles like
                    // "Surround 5.1" on the original tracks.
                    maps.Append($"-map 0:{src.Index} ");
                    codecArgs.Append($"-c:a:{outIndex} copy ");
                }
                outIndex++;
            }

            // -- Then emit re-encodes in profile order. --------------------------------
            foreach (var re in reencodes)
            {
                maps.Append($"-map 0:{re.srcIndex} ");
                codecArgs.Append(BuildAudioCodecArgs(re.codec, re.channels, re.bitrateKbps, outIndex)).Append(' ');
                AppendAudioMeta(meta, outIndex, bucket, re.codec, re.channels);
                outIndex++;
            }
        }

        if (outIndex == 0) return "";

        // Auto-default: flag the first kept output (top-priority language) as the
        // default audio track, clear default on the rest. Emitted after the meta block
        // because -disposition resets all disposition flags for that stream — we want
        // it to win against any "default=1" carried in via -c:a copy.
        var disposition = new StringBuilder();
        if (autoSetDefault)
        {
            disposition.Append("-disposition:a:0 default ");
            for (int i = 1; i < outIndex; i++) disposition.Append($"-disposition:a:{i} 0 ");
        }

        return (maps.ToString() + codecArgs.ToString() + meta.ToString() + disposition.ToString()).TrimEnd();
    }

    /// <summary>
    ///     Stamps an encoded output stream with <c>language=</c> + a
    ///     <c>"Language (CODEC LAYOUT)"</c> title (e.g. <c>"English (Opus 5.1)"</c>) so
    ///     players display a meaningful track name instead of falling back to the source's
    ///     stale title (which might still say "5.1 Surround" after a stereo downmix).
    ///     Copies skip this — their metadata flows through with <c>-c:a copy</c>.
    /// </summary>
    private static void AppendAudioMeta(StringBuilder meta, int outIndex, string bucket, string codec, int channels)
    {
        var langTag = LanguageMatcher.ToThreeLetterB(bucket);
        if (!string.IsNullOrEmpty(langTag) && !string.Equals(langTag, "und", StringComparison.OrdinalIgnoreCase))
            meta.Append($"-metadata:s:a:{outIndex} language={langTag} ");

        var langName = LanguageMatcher.ToEnglishName(bucket);
        var prefix   = !string.IsNullOrEmpty(langName) ? langName : "Audio";
        var layout   = ChannelsToLayoutLabel(channels);
        var suffix   = string.IsNullOrEmpty(layout) ? CodecLabel(codec) : $"{CodecLabel(codec)} {layout}";
        meta.Append($"-metadata:s:a:{outIndex} title=\"{prefix} ({suffix})\" ");
    }

    /// <summary> Display label for a codec name in track titles. </summary>
    private static string CodecLabel(string codec) => (codec ?? "").ToLowerInvariant() switch
    {
        "aac"  => "AAC",
        "ac3"  => "AC3",
        "eac3" => "E-AC3",
        "opus" => "Opus",
        _      => (codec ?? "").ToUpperInvariant(),
    };

    /// <summary>
    ///     Channel count → human-readable layout label for track titles. Falls back to
    ///     <c>"{N}ch"</c> for unusual counts so 4.0 quad / 6.1 still surface something
    ///     meaningful instead of nothing. Returns empty for non-positive counts so the
    ///     title omits the layout segment entirely rather than printing "(Codec )".
    /// </summary>
    private static string ChannelsToLayoutLabel(int channels) => channels switch
    {
        <= 0 => "",
        1    => "Mono",
        2    => "Stereo",
        6    => "5.1",
        8    => "7.1",
        _    => $"{channels}ch",
    };

    /// <summary>
    ///     Resolves a requested codec + channel count into what FFmpeg will actually run,
    ///     given the output container. Falls back to AAC when the codec is unknown or
    ///     unsupported by the container, and clamps channel count to the codec's ceiling.
    ///     Each fallback/clamp appends a human-readable line to <paramref name="warnings"/>.
    /// </summary>
    private static (string codec, int channels) ResolveAudioCodec(
        string                  requested,
        int                     channels,
        string                  container,
        List<string>            warnings)
    {
        var codec = (requested ?? "").Trim().ToLowerInvariant();
        bool webm = IsWebm(container);
        bool mp4  = IsMp4(container);

        if (!_codecSpecs.TryGetValue(codec, out var spec))
        {
            string unknownFallback = webm ? "opus" : "aac";
            warnings.Add($"Audio: unknown codec '{requested}' — falling back to {unknownFallback.ToUpperInvariant()}.");
            codec = unknownFallback;
            spec  = _codecSpecs[codec];
        }

        if (mp4 && !spec.AllowedInMp4)
        {
            warnings.Add($"Audio: codec '{codec}' is not supported in MP4 — falling back to AAC.");
            codec = "aac";
            spec  = _codecSpecs["aac"];
        }
        else if (webm && !spec.AllowedInWebm)
        {
            warnings.Add($"Audio: codec '{codec}' is not supported in WebM — falling back to Opus.");
            codec = "opus";
            spec  = _codecSpecs["opus"];
        }

        if (channels > spec.MaxChannels)
        {
            warnings.Add($"Audio: codec '{codec}' supports up to {spec.MaxChannels} channels; clamping from {channels}.");
            channels = spec.MaxChannels;
        }

        return (codec, channels);
    }

    /// <summary>
    ///     Builds per-output-stream FFmpeg flags for a given codec/channel/bitrate target.
    ///     The output-stream index is baked into <c>-c:a:N</c>, <c>-b:a:N</c>, etc., so
    ///     callers can interleave many audio outputs in a single command.
    /// </summary>
    private static string BuildAudioCodecArgs(string codec, int channels, int bitrateKbps, int outIndex)
    {
        if (!_codecSpecs.TryGetValue(codec, out var spec)) spec = _codecSpecs["aac"];

        var sb = new StringBuilder();
        sb.Append($"-c:a:{outIndex} {spec.Encoder}");

        var br = bitrateKbps > 0 ? bitrateKbps : spec.DefaultBitrateKbps;
        switch (spec.BitrateStyle)
        {
            case AudioBitrateStyle.Cbr:
                sb.Append($" -b:a:{outIndex} {br}k");
                break;
            case AudioBitrateStyle.OpusVbr:
                sb.Append($" -b:a:{outIndex} {br}k -vbr:a:{outIndex} on");
                break;
            case AudioBitrateStyle.None:
                break;
        }

        if (channels > 0) sb.Append($" -ac:a:{outIndex} {channels}");
        return sb.ToString();
    }

    /// <summary>
    ///     Whether a track title looks like a commentary track. Loose substring match
    ///     ("comm") is intentional — real-world commentary tracks ship with titles like
    ///     "Director Comm", "Comm Track", "Filmmaker Commentary", etc., and a stricter
    ///     match would let some slip through. The rule is "commentary is always dropped"
    ///     so both <see cref="MapAudio"/> and <c>TranscodingService.HasAudioWork</c>
    ///     consult this helper to stay aligned.
    /// </summary>
    internal static bool IsCommentaryTitle(string? title) =>
        title != null && title.ToLowerInvariant().Contains("comm");

    /// <summary> True when <paramref name="container"/> is the WebM container. </summary>
    internal static bool IsWebm(string? container) =>
        string.Equals(container, "webm", StringComparison.OrdinalIgnoreCase);

    /// <summary> True when <paramref name="container"/> is the MP4 container. </summary>
    internal static bool IsMp4(string? container) =>
        string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary> True when <paramref name="container"/> is the Matroska container. </summary>
    internal static bool IsMatroska(string? container) =>
        string.Equals(container, "mkv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether <paramref name="container"/> can stream-copy a source audio codec. Used during
    ///     pass-through to decide between <c>-c:a copy</c> and a re-encode fallback. Matroska is
    ///     permissive enough that we don't gate copies for it; MP4 and WebM both have strict
    ///     allowed-codec lists.
    /// </summary>
    internal static bool ContainerCanCopySource(string? sourceCodec, string container)
    {
        var c = (sourceCodec ?? "").ToLowerInvariant();
        if (IsWebm(container))
        {
            // WebM officially allows only Opus and Vorbis audio.
            return c is "opus" or "vorbis";
        }
        if (IsMp4(container))
        {
            return c is "aac" or "ac3" or "eac3" or "mp3" or "alac";
            // truehd, dts, dtshd, flac, opus, pcm_*: not safe to copy into MP4
        }
        // Matroska (or unknown): permissive — copy anything.
        return true;
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
    ///     By default drops bitmap subtitle codecs (PGS, VOBSUB, DVB) that can cause encoding failures.
    ///     Returns <c>-sn</c> (no subtitles) for MP4 containers, which don't support text subtitle passthrough.
    /// </summary>
    /// <param name="probe">The ffprobe analysis of the source file.</param>
    /// <param name="languagesToKeep">
    ///     2-letter ISO codes to retain. Empty or null keeps every (non-bitmap) subtitle stream. Matching
    ///     accepts the track's tag in any of its common forms (2-letter, 3-letter, or English name).
    /// </param>
    /// <param name="container">
    ///     Output container token ("mkv", "mp4", or "webm"). Only Matroska preserves text subtitles;
    ///     MP4 and WebM both emit <c>-sn</c> to strip all subs.
    /// </param>
    /// <param name="includeBitmaps">
    ///     When <c>true</c> AND the container is Matroska, bitmap subs (PGS/VOBSUB/DVB) are passed through
    ///     instead of dropped. <c>-c:s copy</c> handles them without re-decoding.
    /// </param>
    /// <param name="excludeSdh">
    ///     When <c>true</c>, drops tracks tagged hearing-impaired via
    ///     <c>disposition.hearing_impaired</c> or an SDH-style title.
    /// </param>
    /// <param name="autoSetDefault">
    ///     When <c>true</c>, flags the first output subtitle as the default track and clears
    ///     default on the others. Combined with the preference-order reorder this makes the
    ///     top-priority language auto-select in players that honor disposition.
    /// </param>
    /// <returns>FFmpeg subtitle mapping arguments, or <c>-sn</c> to strip all subtitles.</returns>
    public string MapSub(
        ProbeResult            probe,
        IReadOnlyList<string>? languagesToKeep,
        string                 container,
        bool                   includeBitmaps = false,
        bool                   excludeSdh     = false,
        bool                   autoSetDefault = false)
    {
        ArgumentNullException.ThrowIfNull(probe);

        // Only Matroska preserves text subtitles. MP4 and WebM both strip everything.
        if (!IsMatroska(container))
            return "-sn";

        var subtitleStreams = probe.Streams.Where(s => s.CodecType == "subtitle").ToList();

        // Bitmap subs (PGS, VOBSUB, DVB) are dropped by default because they have historically
        // caused hangs and encoding failures; opt-in pass-through keeps them when the user wants
        // a faithful Blu-ray-style remux.
        var keepSubs = includeBitmaps
            ? subtitleStreams
            : subtitleStreams.Where(s => !_bitmapSubCodecs.Contains(s.CodecName ?? "")).ToList();

        if (excludeSdh)
            keepSubs = keepSubs.Where(s => !IsHearingImpaired(s)).ToList();

        bool haveKeepList = languagesToKeep != null && languagesToKeep.Count > 0;
        if (haveKeepList)
        {
            keepSubs = keepSubs
                .Where(s => LanguageMatcher.Matches(s.Tags?.Language, s.Tags?.Title, languagesToKeep!))
                .ToList();

            // Reorder by user preference: tracks whose language sits earlier in the keep-list
            // come first. Within a single language bucket we preserve source order so multiple
            // sub variants for the same language stay together. Unresolvable languages
            // (shouldn't happen after the filter, but defensive) sort last.
            keepSubs = keepSubs
                .Select((s, srcIdx) => new { s, srcIdx, prefIdx = PreferenceIndex(s, languagesToKeep!) })
                .OrderBy(x => x.prefIdx)
                .ThenBy(x => x.srcIdx)
                .Select(x => x.s)
                .ToList();
        }

        if (keepSubs.Count == 0) return "-sn";

        var sb = new StringBuilder();
        foreach (var s in keepSubs) sb.Append("-map 0:").Append(s.Index).Append(' ');
        sb.Append("-c:s copy");

        if (autoSetDefault)
        {
            sb.Append(" -disposition:s:0 default");
            for (int i = 1; i < keepSubs.Count; i++)
                sb.Append(" -disposition:s:").Append(i).Append(" 0");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     <c>true</c> when a subtitle stream is tagged hearing-impaired, either by
    ///     ffprobe's <c>disposition.hearing_impaired</c> flag or by a descriptive title
    ///     matching the SDH/CC patterns in <see cref="LanguageMatcher.IsSdhTitle"/>.
    ///     The title fallback catches Blu-ray bitmap rips where the disposition flag
    ///     is rarely set but the track name says "English [SDH]".
    /// </summary>
    internal static bool IsHearingImpaired(Stream s) =>
        (s.Disposition?.HearingImpaired ?? 0) == 1
        || LanguageMatcher.IsSdhTitle(s.Tags?.Title);

    /// <summary>
    ///     Index of a stream's language within <paramref name="languagesToKeep"/>, or
    ///     <see cref="int.MaxValue"/> when it can't be resolved. Used as the primary sort key
    ///     in <see cref="MapSub"/> so the user's preference order becomes the output order.
    /// </summary>
    private static int PreferenceIndex(Stream s, IReadOnlyList<string> languagesToKeep)
    {
        var two = LanguageMatcher.ToTwoLetter(s.Tags?.Language)
               ?? LanguageMatcher.InferFromTitle(s.Tags?.Title);
        if (two == null) return int.MaxValue;

        for (int i = 0; i < languagesToKeep.Count; i++)
        {
            var wantedTwo = LanguageMatcher.ToTwoLetter(languagesToKeep[i]) ?? languagesToKeep[i];
            if (string.Equals(wantedTwo, two, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return int.MaxValue;
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
    /// <param name="excludeSdh">       When <c>true</c>, drops tracks tagged hearing-impaired. </param>
    public IReadOnlyList<SidecarSpec> SelectSidecarStreams(
        ProbeResult            probe,
        IReadOnlyList<string>? languagesToKeep,
        bool                   includeBitmaps,
        bool                   excludeSdh = false)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var subs = probe.Streams.Where(s => s.CodecType == "subtitle").ToList();
        if (excludeSdh) subs = subs.Where(s => !IsHearingImpaired(s)).ToList();
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
    ///     Validates a transcoded file by comparing pre-computed input and output durations.
    ///     Allows up to 30 seconds or 1% of total duration as tolerance.
    ///     Returns <c>true</c> if the output duration is unreadable (trusts FFmpeg's exit code).
    /// </summary>
    /// <param name="inputDuration">Duration of the source file in seconds.</param>
    /// <param name="outputDuration">Duration of the transcoded output file in seconds.</param>
    /// <returns><c>true</c> if the output appears valid; <c>false</c> if durations diverge beyond tolerance.</returns>
    public bool ConvertedSuccessfully(double inputDuration, double outputDuration)
    {
        // If we can't read the output duration (common with freshly written MKV),
        // trust that the encode completed since FFmpeg exited successfully.
        if (outputDuration <= 0)
            return true;

        double durationDifference = Math.Abs(inputDuration - outputDuration);

        // Allow up to 30 seconds or 1% of total duration, whichever is greater.
        double tolerance = Math.Max(30, inputDuration * 0.01);
        return durationDifference < tolerance;
    }

    /// <summary>
    ///     Convenience overload that reads durations from container header metadata.
    ///     Use the <see cref="ConvertedSuccessfully(double, double)"/> overload with values
    ///     from <see cref="GetAccurateVideoDurationAsync"/> when validating real encoded
    ///     content — header metadata can lie on broken sources.
    /// </summary>
    public bool ConvertedSuccessfully(ProbeResult input, ProbeResult output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            return ConvertedSuccessfully(GetVideoDuration(input), GetVideoDuration(output));
        }
        catch
        {
            // Trust FFmpeg's exit code if probe fails.
            return true;
        }
    }

    /// <summary>
    ///     Decides whether an output that failed <see cref="ConvertedSuccessfully(double, double)"/>
    ///     should still be accepted because the missing tail of the source is just blank/black
    ///     padding. Runs <see cref="AnalyzeTailBlackAsync"/> over the gap between the output's end
    ///     and the source's measured end and returns true when ≥95% of that range is black.
    ///     Pass durations measured by <see cref="GetAccurateVideoDurationAsync"/> — header-only
    ///     values can produce a window past real EOF where blackdetect finds nothing.
    /// </summary>
    public async Task<bool> IsTailMostlyBlackAsync(
        string            sourcePath,
        double            outputDuration,
        double            sourceDuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        if (outputDuration <= 0 || sourceDuration <= outputDuration + 1) return false;

        var (black, tail, ok) = await AnalyzeTailBlackAsync(sourcePath, outputDuration, sourceDuration, ct);
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

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var flag in new[]
        {
            "-v", "info", "-nostats",
            "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture),
            "-to", clampedEnd.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-map", "0:v:0", "-vf", "blackdetect=d=0.1:pic_th=0.98",
            "-an", "-sn", "-f", "null", "-",
        })
            psi.ArgumentList.Add(flag);

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
    ///     Reads the real end-of-content time of the first video stream by probing the last
    ///     packets of the file directly, rather than reading container header metadata
    ///     (which can be inaccurate on broken sources or freshly-muxed outputs). Falls back
    ///     to <see cref="GetVideoDuration"/> on the supplied probe if the packet probe fails.
    /// </summary>
    /// <param name="filePath">Absolute path to the media file.</param>
    /// <param name="fallbackProbe">Optional previously-obtained probe; supplies the fallback duration if the packet probe fails.</param>
    /// <param name="ct">Cancellation token. Cancellation kills the ffprobe process tree.</param>
    /// <returns>End-of-content time in seconds, or 0 if no duration can be determined.</returns>
    public async Task<double> GetAccurateVideoDurationAsync(
        string            filePath,
        ProbeResult?      fallbackProbe = null,
        CancellationToken ct            = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        // 99999%+#100 seeks ffprobe to EOF and reads only the last 100 packets, so this
        // stays fast on multi-GB files while still surfacing the real last-frame PTS.
        var psi = new ProcessStartInfo(_ffprobePath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var flag in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-read_intervals", "99999%+#100",
            "-show_entries", "packet=pts_time,duration_time", "-of", "json",
        })
            psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(filePath);

        var stdout = new StringBuilder();
        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) stdout.AppendLine(e.Data);
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            // Drain stderr so the buffer can't fill and stall the process.
            _ = proc.StandardError.ReadToEndAsync(ct);

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
            return GetVideoDuration(fallbackProbe ?? new ProbeResult());
        }

        try
        {
            string json = stdout.ToString();
            int braceIndex = json.IndexOf('{');
            if (braceIndex < 0) return GetVideoDuration(fallbackProbe ?? new ProbeResult());
            json = json.Substring(braceIndex);

            var packets = JObject.Parse(json)["packets"] as JArray;
            if (packets == null || packets.Count == 0)
                return GetVideoDuration(fallbackProbe ?? new ProbeResult());

            double maxEnd = 0;
            foreach (var pkt in packets)
            {
                if (!double.TryParse(pkt["pts_time"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pts))
                    continue;
                double dur = 0;
                double.TryParse(pkt["duration_time"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                double end = pts + dur;
                if (end > maxEnd) maxEnd = end;
            }

            if (maxEnd > 0) return maxEnd;
        }
        catch
        {
            // Fall through to fallback on malformed JSON.
        }

        return GetVideoDuration(fallbackProbe ?? new ProbeResult());
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
            // Split only on ':' first to preserve fractional seconds in the last component.
            // ffprobe/ffmpeg always emit '.' as the decimal separator regardless of host
            // locale, so parse against InvariantCulture — current-culture parsing on
            // (e.g.) de-DE would treat the '.' as a thousands separator and either fail
            // or return a value 10^N too large.
            string[] colonParts = input.Split(':');
            if (colonParts.Length >= 3)
            {
                return double.Parse(colonParts[0], NumberStyles.Float, CultureInfo.InvariantCulture) * 3600
                     + double.Parse(colonParts[1], NumberStyles.Float, CultureInfo.InvariantCulture) * 60
                     + double.Parse(colonParts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else
            {
                return double.Parse(input, NumberStyles.Float, CultureInfo.InvariantCulture);
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