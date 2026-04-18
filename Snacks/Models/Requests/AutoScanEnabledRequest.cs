namespace Snacks.Models.Requests;

/// <summary> Request body for enabling or disabling the auto-scan service. </summary>
public sealed class AutoScanEnabledRequest
{
    /// <summary> Whether auto-scan should be active. </summary>
    public bool Enabled { get; set; }
}
