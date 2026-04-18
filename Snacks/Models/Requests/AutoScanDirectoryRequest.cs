namespace Snacks.Models.Requests;

/// <summary> Request body for adding or removing a directory from the auto-scan watch list. </summary>
public sealed class AutoScanDirectoryRequest
{
    /// <summary> Absolute path to the directory. </summary>
    public string Path { get; set; } = "";
}
