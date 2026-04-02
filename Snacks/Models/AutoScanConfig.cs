namespace Snacks.Models
{
    public class AutoScanConfig
    {
        public bool Enabled { get; set; } = false;
        public int IntervalMinutes { get; set; } = 60;
        public List<string> Directories { get; set; } = new();
        public DateTime? LastScanTime { get; set; }
        public int LastScanNewFiles { get; set; } = 0;
        public bool QueuePaused { get; set; } = false;
    }
}
