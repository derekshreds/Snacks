namespace Snacks.Models
{
    public class JobAssignment
    {
        public string JobId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public EncoderOptions Options { get; set; } = new();
        public ProbeResult? Probe { get; set; }
        public double Duration { get; set; }
        public long Bitrate { get; set; }
        public bool IsHevc { get; set; }
    }

    public class JobProgress
    {
        public string JobId { get; set; } = "";
        public int Progress { get; set; }
        public string? LogLine { get; set; }
        public string? Phase { get; set; } // "Uploading", "Encoding", "Downloading"
    }

    public class JobCompletion
    {
        public string JobId { get; set; } = "";
        public bool Success { get; set; }
        public long OutputFileSize { get; set; }
        public string? ErrorMessage { get; set; }
        public string OutputFileName { get; set; } = "";
    }
}
