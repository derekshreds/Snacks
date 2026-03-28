using Newtonsoft.Json;

namespace Snacks.Models
{
    /// <summary> Json deserialization structure for ffprobe tags </summary>
    public class Tags
    {
        public string? language { get; set; }
        public string? title { get; set; }
        public string? handler_name { get; set; }
        public string? vendor_id { get; set; }
    }

    /// <summary> Json deserialization structure for ffprobe streams </summary>
    public class Stream
    {
        public int index { get; set; }
        public string codec_name { get; set; } = "";
        public string? codec_long_name { get; set; }
        public string? profile { get; set; }
        public string codec_type { get; set; } = "";
        public string? codec_time_base { get; set; }
        public string? codec_tag_string { get; set; }
        public string? codec_tag { get; set; }
        public int channels { get; set; }
        public string? channel_layout { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public int coded_width { get; set; }
        public int coded_height { get; set; }
        public int has_b_frames { get; set; }
        public string? sample_aspect_ratio { get; set; }
        public string? display_aspect_ratio { get; set; }
        public string? pix_fmt { get; set; }
        public int level { get; set; }
        public string? color_range { get; set; }
        public string? color_space { get; set; }
        public string? color_transfer { get; set; }
        public string? color_primaries { get; set; }
        public string? chroma_location { get; set; }
        public string? field_order { get; set; }
        public string? refs { get; set; }
        public string? is_avc { get; set; }
        public string? nal_length_size { get; set; }
        public string? r_frame_rate { get; set; }
        public string? avg_grame_rate { get; set; }
        public string? time_base { get; set; }
        public string? start_pts { get; set; }
        public string? start_time { get; set; }
        public long duration_ts { get; set; }
        public string? duration { get; set; }
        public string? bit_rate { get; set; }
        public string? nb_frames { get; set; }
        public string? bits_per_raw_sample { get; set; }
        public Tags? tags { get; set; }
    }

    /// <summary> Json deserialization structure for ffprobe format </summary>
    public class Format
    {
        public string? filename { get; set; }
        public int nb_streams { get; set; }
        public int nb_programs { get; set; }
        public string? format_name { get; set; }
        public string? format_long_name { get; set; }
        public string? start_time { get; set; }
        public string? duration { get; set; }
        public string? size { get; set; }
        public string? bit_rate { get; set; }
        public int probe_score { get; set; }
    }

    /// <summary> Json deserialization structure for the probe result </summary>
    public class ProbeResult
    {
        public Stream[] streams { get; set; } = Array.Empty<Stream>();
        public Packet[] packets { get; set; } = Array.Empty<Packet>();
        public Format format { get; set; } = new Format();
        public long AudioBitrate { get; set; }
    }

    public class Packet
    {
        public string? codec_type { get; set; }
        public int stream_index { get; set; }
        public string? pts { get; set; }
        public string? dts { get; set; }
        public string? duration_ts { get; set; }
        public string? duration { get; set; }
        public string? size { get; set; }
        public string? pos { get; set; }
        public Tags? tags { get; set; }
    }
}