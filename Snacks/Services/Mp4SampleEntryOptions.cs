namespace Snacks.Services;

/// <summary>
///     Recognises the ffmpeg option tokens that decide an MP4 video sample entry, so the
///     transcoder and the advanced profile validator apply one rule when deciding whether a
///     profile author has taken ownership of the tag.
/// </summary>
public static class Mp4SampleEntryOptions
{
    /// <summary> Compliance level at which the MP4 muxer writes the Dolby Vision box. </summary>
    public const string DolbyVisionComplianceLevel = "unofficial";

    /// <summary> The ffmpeg option that sets the muxer's compliance level. </summary>
    public const string ComplianceOption = "-strict";

    /// <summary> ffprobe's side data label for a Dolby Vision configuration record. </summary>
    public const string DolbyVisionSideDataType = "DOVI configuration record";

    /// <summary> Whether <paramref name="token" /> sets the video codec tag. </summary>
    /// <param name="token"> One command-line token. </param>
    public static bool IsVideoTagOption(string? token)
    {
        var option = token?.Trim() ?? "";
        return option.Equals("-tag", StringComparison.OrdinalIgnoreCase)
            || option.Equals("-vtag", StringComparison.OrdinalIgnoreCase)
            || option.StartsWith("-tag:v", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> Whether <paramref name="token" /> is the ffmpeg compliance option. </summary>
    /// <param name="token"> One command-line token. </param>
    public static bool IsComplianceOption(string? token) =>
        string.Equals(token?.Trim(), ComplianceOption, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether <paramref name="token" /> drives a Dolby Vision encode: x265's
    ///     <c>dolby-vision-*</c> parameters or ffmpeg's <c>-dolbyvision</c> option, as an option
    ///     name or inside a value.
    /// </summary>
    /// <param name="token"> One command-line token. </param>
    public static bool MentionsDolbyVision(string? token) =>
        (token ?? "").Contains("dolby", StringComparison.OrdinalIgnoreCase);
}
