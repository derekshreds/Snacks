namespace Snacks.Models.Requests;

/// <summary> Request body for processing every video file in a directory. </summary>
public sealed class ProcessDirectoryRequest
{
    /// <summary> Absolute path to the directory to process. </summary>
    public string DirectoryPath { get; set; } = "";

    /// <summary> Whether to recurse into subdirectories. Defaults to <see langword="true"/>. </summary>
    public bool Recursive { get; set; } = true;

    /// <summary> Encoder options to apply to all files in the directory. </summary>
    public EncoderOptions Options { get; set; } = new();
}
