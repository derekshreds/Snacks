namespace Snacks.Models.Requests;

/// <summary> Request body for processing a single video file. </summary>
public sealed class ProcessFileRequest
{
    /// <summary> Absolute path to the file to process. </summary>
    public string FilePath { get; set; } = "";

    /// <summary> Encoder options to apply to the file. </summary>
    public EncoderOptions Options { get; set; } = new();
}
