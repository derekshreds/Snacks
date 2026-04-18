namespace Snacks.Models.Requests;

/// <summary> Request body for saving per-folder encoding override settings. </summary>
public sealed class SaveFolderSettingsRequest
{
    /// <summary> Absolute path to the watched folder. </summary>
    public string Path { get; set; } = "";

    /// <summary> Encoder overrides to apply to files in this folder, or <see langword="null"/> to clear overrides. </summary>
    public EncoderOptionsOverride? EncodingOverrides { get; set; }
}
