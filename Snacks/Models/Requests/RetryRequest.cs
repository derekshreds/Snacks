namespace Snacks.Models.Requests;

/// <summary> Request body for re-queuing a previously failed file. </summary>
public sealed class RetryRequest
{
    /// <summary> Absolute path to the file to retry. </summary>
    public string FilePath { get; set; } = "";
}
