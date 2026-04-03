namespace Snacks.Models;

using Newtonsoft.Json;

/// <summary>
///     Complete ffprobe analysis result for a media file.
///     Contains all streams, packets, and format information
///     needed to build FFmpeg command lines and validate output.
/// </summary>
public sealed class ProbeResult
{
    /// <summary> All media streams (video, audio, subtitle) in the file. </summary>
    [JsonProperty("streams")]
    public Stream[] Streams { get; set; } = Array.Empty<Stream>();

    /// <summary> Packet-level information (used for detailed analysis). </summary>
    [JsonProperty("packets")]
    public Packet[] Packets { get; set; } = Array.Empty<Packet>();

    /// <summary> Container format information. </summary>
    [JsonProperty("format")]
    public Format Format { get; set; } = new();

    /// <summary> Combined audio bitrate across all audio streams. </summary>
    public long AudioBitrate { get; set; }
}

/// <summary>
///     Metadata tags extracted from media streams by ffprobe.
///     Contains language, title, and vendor information for streams.
/// </summary>
public sealed class Tags
{
    /// <summary> ISO 639-2 language code (e.g., "eng", "jpn"). </summary>
    [JsonProperty("language")]
    public string? Language { get; set; }

    /// <summary> Stream title (e.g., "Commentary", "Director's Cut"). </summary>
    [JsonProperty("title")]
    public string? Title { get; set; }

    /// <summary> Handler name from the muxer. </summary>
    [JsonProperty("handler_name")]
    public string? HandlerName { get; set; }

    /// <summary> Vendor identifier string. </summary>
    [JsonProperty("vendor_id")]
    public string? VendorId { get; set; }
}

/// <summary>
///     Represents a single media, audio, or subtitle stream
///     as reported by ffprobe. Contains codec, resolution,
///     timing, and format details.
/// </summary>
public sealed class Stream
{
    /// <summary> Zero-based index of this stream within the file. </summary>
    [JsonProperty("index")]
    public int Index { get; set; }

    /// <summary> Codec name (e.g., "h264", "hevc", "aac", "ac3"). </summary>
    [JsonProperty("codec_name")]
    public string CodecName { get; set; } = "";

    /// <summary> Full codec description (e.g., "H.264 / AVC / MPEG-4 AVC"). </summary>
    [JsonProperty("codec_long_name")]
    public string? CodecLongName { get; set; }

    /// <summary> Codec profile (e.g., "Main", "High", "Main 10"). </summary>
    [JsonProperty("profile")]
    public string? Profile { get; set; }

    /// <summary> Stream type: "video", "audio", "subtitle", or "data". </summary>
    [JsonProperty("codec_type")]
    public string CodecType { get; set; } = "";

    /// <summary> Codec time base as a fraction string. </summary>
    [JsonProperty("codec_time_base")]
    public string? CodecTimeBase { get; set; }

    /// <summary> Codec tag string (e.g., "[0][0][0][0]"). </summary>
    [JsonProperty("codec_tag_string")]
    public string? CodecTagString { get; set; }

    /// <summary> Codec tag hex string (e.g., "0x0000"). </summary>
    [JsonProperty("codec_tag")]
    public string? CodecTag { get; set; }

    /// <summary> Number of audio channels (0 for video streams). </summary>
    [JsonProperty("channels")]
    public int Channels { get; set; }

    /// <summary> Audio channel layout (e.g., "5.1(side)"). </summary>
    [JsonProperty("channel_layout")]
    public string? ChannelLayout { get; set; }

    /// <summary> Video width in pixels (0 for non-video streams). </summary>
    [JsonProperty("width")]
    public int Width { get; set; }

    /// <summary> Video height in pixels (0 for non-video streams). </summary>
    [JsonProperty("height")]
    public int Height { get; set; }

    /// <summary> Coded width (may include padding). </summary>
    [JsonProperty("coded_width")]
    public int CodedWidth { get; set; }

    /// <summary> Coded height (may include padding). </summary>
    [JsonProperty("coded_height")]
    public int CodedHeight { get; set; }

    /// <summary> Number of B-frames in the stream. </summary>
    [JsonProperty("has_b_frames")]
    public int HasBFrames { get; set; }

    /// <summary> Sample aspect ratio (e.g., "1:1"). </summary>
    [JsonProperty("sample_aspect_ratio")]
    public string? SampleAspectRatio { get; set; }

    /// <summary> Display aspect ratio (e.g., "16:9"). </summary>
    [JsonProperty("display_aspect_ratio")]
    public string? DisplayAspectRatio { get; set; }

    /// <summary> Pixel format (e.g., "yuv420p", "yuv420p10le"). </summary>
    [JsonProperty("pix_fmt")]
    public string? PixFmt { get; set; }

    /// <summary> Codec level (e.g., 40 for H.264 Level 4.0). </summary>
    [JsonProperty("level")]
    public int Level { get; set; }

    /// <summary> Color range: "tv" (limited) or "pc" (full). </summary>
    [JsonProperty("color_range")]
    public string? ColorRange { get; set; }

    /// <summary> Color space (e.g., "bt709", "bt2020nc"). </summary>
    [JsonProperty("color_space")]
    public string? ColorSpace { get; set; }

    /// <summary> Color transfer characteristics (e.g., "smpte2084" for HDR). </summary>
    [JsonProperty("color_transfer")]
    public string? ColorTransfer { get; set; }

    /// <summary> Color primaries (e.g., "bt2020"). </summary>
    [JsonProperty("color_primaries")]
    public string? ColorPrimaries { get; set; }

    /// <summary> Chroma sample location. </summary>
    [JsonProperty("chroma_location")]
    public string? ChromaLocation { get; set; }

    /// <summary> Field order for interlaced content. </summary>
    [JsonProperty("field_order")]
    public string? FieldOrder { get; set; }

    /// <summary> Number of reference frames. </summary>
    [JsonProperty("refs")]
    public string? Refs { get; set; }

    /// <summary> Whether the stream is AVC/H.264 ("true"/"false"). </summary>
    [JsonProperty("is_avc")]
    public string? IsAvc { get; set; }

    /// <summary> NAL unit length size (for AVC). </summary>
    [JsonProperty("nal_length_size")]
    public string? NalLengthSize { get; set; }

    /// <summary> Real frame rate (e.g., "24000/1001"). </summary>
    [JsonProperty("r_frame_rate")]
    public string? RFrameRate { get; set; }

    /// <summary> Average frame rate. </summary>
    [JsonProperty("avg_frame_rate")]
    public string? AvgFrameRate { get; set; }

    /// <summary> Time base as a fraction string. </summary>
    [JsonProperty("time_base")]
    public string? TimeBase { get; set; }

    /// <summary> Starting presentation timestamp. </summary>
    [JsonProperty("start_pts")]
    public string? StartPts { get; set; }

    /// <summary> Starting time in seconds. </summary>
    [JsonProperty("start_time")]
    public string? StartTime { get; set; }

    /// <summary> Duration in timebase units. </summary>
    [JsonProperty("duration_ts")]
    public long DurationTs { get; set; }

    /// <summary> Duration in seconds as a string. </summary>
    [JsonProperty("duration")]
    public string? Duration { get; set; }

    /// <summary> Bitrate in bits/second. </summary>
    [JsonProperty("bit_rate")]
    public string? BitRate { get; set; }

    /// <summary> Number of frames in the stream. </summary>
    [JsonProperty("nb_frames")]
    public string? NbFrames { get; set; }

    /// <summary> Bits per raw sample. </summary>
    [JsonProperty("bits_per_raw_sample")]
    public string? BitsPerRawSample { get; set; }

    /// <summary> Stream metadata tags (language, title, etc.). </summary>
    [JsonProperty("tags")]
    public Tags? Tags { get; set; }
}

/// <summary>
///     Container format information extracted by ffprobe.
///     Describes the file-level metadata (format, duration, size).
/// </summary>
public sealed class Format
{
    /// <summary> Input filename. </summary>
    [JsonProperty("filename")]
    public string? Filename { get; set; }

    /// <summary> Number of streams in the file. </summary>
    [JsonProperty("nb_streams")]
    public int NbStreams { get; set; }

    /// <summary> Number of programs (0 for most formats). </summary>
    [JsonProperty("nb_programs")]
    public int NbPrograms { get; set; }

    /// <summary> Format short name (e.g., "matroska,webm", "mov"). </summary>
    [JsonProperty("format_name")]
    public string? FormatName { get; set; }

    /// <summary> Format long name (e.g., "Matroska / WebM"). </summary>
    [JsonProperty("format_long_name")]
    public string? FormatLongName { get; set; }

    /// <summary> Start time in seconds. </summary>
    [JsonProperty("start_time")]
    public string? StartTime { get; set; }

    /// <summary> Total duration in seconds. </summary>
    [JsonProperty("duration")]
    public string? Duration { get; set; }

    /// <summary> File size in bytes. </summary>
    [JsonProperty("size")]
    public string? Size { get; set; }

    /// <summary> Overall bitrate in bits/second. </summary>
    [JsonProperty("bit_rate")]
    public string? BitRate { get; set; }

    /// <summary> Probe score (higher = more confident detection). </summary>
    [JsonProperty("probe_score")]
    public int ProbeScore { get; set; }
}

/// <summary>
///     Represents a single packet from ffprobe packet analysis.
///     Used for detailed stream timing and size analysis.
/// </summary>
public sealed class Packet
{
    /// <summary> Packet codec type: "video", "audio", "subtitle". </summary>
    [JsonProperty("codec_type")]
    public string? CodecType { get; set; }

    /// <summary> Index of the stream this packet belongs to. </summary>
    [JsonProperty("stream_index")]
    public int StreamIndex { get; set; }

    /// <summary> Presentation timestamp. </summary>
    [JsonProperty("pts")]
    public string? Pts { get; set; }

    /// <summary> Decoding timestamp. </summary>
    [JsonProperty("dts")]
    public string? Dts { get; set; }

    /// <summary> Packet duration in timebase units. </summary>
    [JsonProperty("duration_ts")]
    public string? DurationTs { get; set; }

    /// <summary> Packet duration in seconds. </summary>
    [JsonProperty("duration")]
    public string? Duration { get; set; }

    /// <summary> Packet size in bytes. </summary>
    [JsonProperty("size")]
    public string? Size { get; set; }

    /// <summary> Byte offset of the packet in the file. </summary>
    [JsonProperty("pos")]
    public string? Pos { get; set; }

    /// <summary> Packet metadata tags. </summary>
    [JsonProperty("tags")]
    public Tags? Tags { get; set; }
}
