namespace Snacks.Models.Requests;

/// <summary> Request body for updating the auto-scan polling interval. </summary>
public sealed class AutoScanIntervalRequest
{
    /// <summary> The new scan interval in minutes. Must be between 1 and 1440. </summary>
    public int IntervalMinutes { get; set; }
}
