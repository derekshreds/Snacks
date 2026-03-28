namespace Snacks.Models
{
    /// <summary> User encoding options </summary>
    public class EncoderOptions
    {
        public string Format { get; set; } = "mkv";
        public string Codec { get; set; } = "h265";
        public string Encoder { get; set; } = "libx265";
        public int TargetBitrate { get; set; } = 3500;
        public bool TwoChannelAudio { get; set; } = false;
        public bool DeleteOriginalFile { get; set; } = false;
        public bool EnglishOnlyAudio { get; set; } = false;
        public bool EnglishOnlySubtitles { get; set; } = false;
        public bool RemoveBlackBorders { get; set; } = false;
        public bool RetryOnFail { get; set; } = true;
        public string? OutputDirectory { get; set; }
        public string? EncodeDirectory { get; set; }
        public bool StrictBitrate { get; set; } = false;
        public string HardwareAcceleration { get; set; } = "auto"; // auto, intel, amd, nvidia, none
    }
}