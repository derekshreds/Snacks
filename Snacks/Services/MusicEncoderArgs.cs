using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Pure ffmpeg-arg builder for music (audio-only) encoding. Companion to
///     <see cref="VideoFilterBuilder"/> on the video side. No process spawning,
///     no I/O — just composes the command-line based on
///     <see cref="MusicEncoderOptions"/> and the source's <see cref="ProbeResult"/>.
/// </summary>
public static class MusicEncoderArgs
{
    /// <summary>
    ///     Maps a logical music codec name to the ffmpeg encoder identifier.
    /// </summary>
    public static string ResolveEncoder(string codec) => codec.ToLowerInvariant() switch
    {
        "libmp3lame" => "libmp3lame",
        "mp3"        => "libmp3lame",
        "aac"        => "aac",
        "libopus"    => "libopus",
        "opus"       => "libopus",
        "libvorbis"  => "libvorbis",
        "vorbis"     => "libvorbis",
        "flac"       => "flac",
        _            => "aac",
    };

    /// <summary>
    ///     Returns the file extension for a music format (without leading dot).
    /// </summary>
    public static string ExtensionForFormat(string format) => format.ToLowerInvariant() switch
    {
        "mp3"  => "mp3",
        "m4a"  => "m4a",
        "ogg"  => "ogg",
        "opus" => "opus",
        "flac" => "flac",
        _      => "m4a",
    };

    /// <summary>
    ///     Returns <see langword="true"/> when the codec writes lossless output and
    ///     should ignore <see cref="MusicEncoderOptions.BitrateKbps"/>.
    /// </summary>
    public static bool IsLossless(string codec) =>
        codec.Equals("flac", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns <see langword="true"/> when the output container muxes a copied
    ///     mjpeg/png "attached picture" video stream (i.e. embedded album art passed
    ///     through with <c>-c:v copy</c> and the <c>attached_pic</c> disposition).
    ///     Opus and Ogg encode pictures via <c>METADATA_BLOCK_PICTURE</c> base64-tagged
    ///     in the Vorbis-comment header instead, which ffmpeg doesn't expose via
    ///     stream-copy — those containers drop the cover-art map silently in v1.
    /// </summary>
    public static bool SupportsAttachedPicStreamCopy(string format) => format.ToLowerInvariant() switch
    {
        "mp3"  => true,
        "m4a"  => true,
        "flac" => true,
        _      => false, // ogg, opus, anything unknown
    };

    /// <summary>
    ///     Returns <see langword="true"/> when the codec name describes a lossy
    ///     audio format. Used by the lossy-to-lossless guard in the skip ladder.
    /// </summary>
    public static bool IsLossy(string codec)
    {
        var c = codec.ToLowerInvariant();
        return c is "mp3" or "aac" or "ac3" or "eac3" or "opus" or "vorbis" or "wma" or "wmav2";
    }

    /// <summary>
    ///     Reads the source audio bitrate (kbps) from the probe — falls back to the
    ///     container-level overall bitrate when the audio stream entry has none.
    /// </summary>
    public static long GetSourceBitrateKbps(ProbeResult probe)
    {
        var audio = probe.Streams.FirstOrDefault(s => s.CodecType == "audio");
        if (audio != null && long.TryParse(audio.BitRate, out var bps) && bps > 0)
            return bps / 1000;

        if (long.TryParse(probe.Format.BitRate, out var fmtBps) && fmtBps > 0)
            return fmtBps / 1000;

        return 0;
    }

    /// <summary>
    ///     Returns the source audio codec name from the probe, or <c>"unknown"</c>
    ///     when no audio stream is present.
    /// </summary>
    public static string GetSourceCodec(ProbeResult probe) =>
        probe.Streams.FirstOrDefault(s => s.CodecType == "audio")?.CodecName ?? "unknown";

    /// <summary>
    ///     Number of audio channels in the first audio stream, or 0 when absent.
    /// </summary>
    public static int GetSourceChannels(ProbeResult probe) =>
        probe.Streams.FirstOrDefault(s => s.CodecType == "audio")?.Channels ?? 0;

    /// <summary>
    ///     Builds the full ffmpeg arg string for a music encode. Caller supplies
    ///     the input and output paths; everything else comes from
    ///     <paramref name="opts"/> and <paramref name="probe"/>.
    /// </summary>
    /// <param name="inputPath"> Source file path (already quoted-escape-safe by caller). </param>
    /// <param name="outputPath"> Destination file path (already quoted-escape-safe by caller). </param>
    /// <param name="opts"> Music encoder options. </param>
    /// <param name="probe"> Source probe; used to detect cover-art video streams. </param>
    public static string Build(string inputPath, string outputPath, MusicEncoderOptions opts, ProbeResult probe)
    {
        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel", "info",
            "-i", $"\"{inputPath}\"",
            "-map", "0:a",
        };

        bool hasCoverArt    = probe.Streams.Any(s => s.CodecType == "video");
        bool containerAcceptsArt = SupportsAttachedPicStreamCopy(opts.Format);
        if (opts.CopyMetadataAndArt && hasCoverArt && containerAcceptsArt)
        {
            // Embedded cover art is exposed as a video stream by ffprobe. Carry it
            // through with -c:v copy and the attached_pic disposition so muxers like
            // mp4/m4a treat it as album art rather than a real video stream. Opus
            // and ogg containers don't support raw mjpeg stream copy — they'd need
            // METADATA_BLOCK_PICTURE encoded in the Vorbis-comment header instead,
            // which ffmpeg doesn't expose via stream-copy semantics. So we explicitly
            // drop the video map for those containers and the cover art is lost in v1.
            args.Add("-map");
            args.Add("0:v?");
            args.Add("-c:v");
            args.Add("copy");
            args.Add("-disposition:v:0");
            args.Add("attached_pic");
        }

        var encoder = ResolveEncoder(opts.Codec);
        args.Add("-c:a");
        args.Add(encoder);

        if (!IsLossless(encoder))
        {
            if (opts.VbrQuality.HasValue)
            {
                args.Add("-q:a");
                args.Add(opts.VbrQuality.Value.ToString());
            }
            else
            {
                args.Add("-b:a");
                args.Add($"{opts.BitrateKbps}k");
            }
        }

        if (opts.SampleRatePolicy != "Source"
            && int.TryParse(opts.SampleRatePolicy, out var sr) && sr > 0)
        {
            args.Add("-ar");
            args.Add(sr.ToString());
        }

        switch (opts.ChannelPolicy)
        {
            case "Mono":   args.Add("-ac"); args.Add("1"); break;
            case "Stereo": args.Add("-ac"); args.Add("2"); break;
        }

        if (opts.CopyMetadataAndArt)
        {
            args.Add("-map_metadata");
            args.Add("0");
        }

        args.Add($"\"{outputPath}\"");
        return string.Join(" ", args);
    }
}
