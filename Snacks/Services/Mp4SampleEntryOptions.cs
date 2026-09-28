using System.Globalization;

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
    ///     Whether the effective <c>-dolbyvision</c> or x265 Dolby Vision parameters request
    ///     a Dolby Vision encode. Explicitly disabled values and unrelated filenames do not
    ///     transfer ownership of the sample entry to the profile.
    /// </summary>
    /// <param name="arguments"> Literal output option and value tokens. </param>
    public static bool EnablesDolbyVision(IReadOnlyList<string> arguments)
    {
        var dolbyVision = LastOptionValue(arguments, "-dolbyvision", allowVideoSpecifier: true);
        if (dolbyVision != null && !IsDisabled(dolbyVision)) return true;

        var x265Parameters = LastOptionValue(arguments, "-x265-params", allowVideoSpecifier: true);
        bool? profileEnabled = null;
        bool hasRpu = false;
        foreach (var parameter in (x265Parameters ?? "").Split(':'))
        {
            var parts = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("dolby-vision-profile", StringComparison.OrdinalIgnoreCase))
                profileEnabled = !IsDisabled(parts[1]);
            else if (parts[0].Equals("dolby-vision-rpu", StringComparison.OrdinalIgnoreCase))
                hasRpu = !string.IsNullOrWhiteSpace(parts[1]);
        }
        return profileEnabled ?? hasRpu;
    }

    /// <summary>
    ///     Whether the last muxer compliance option permits writing a Dolby Vision record.
    ///     FFmpeg accepts unofficial (-1), experimental (-2), or a lower numeric level.
    /// </summary>
    /// <param name="arguments"> Literal output option and value tokens. </param>
    public static bool AllowsDolbyVisionConfiguration(IReadOnlyList<string> arguments)
    {
        var value = LastOptionValue(arguments, ComplianceOption)?.Trim();
        return string.Equals(value, DolbyVisionComplianceLevel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "experimental", StringComparison.OrdinalIgnoreCase)
            || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                && level <= -1);
    }

    /// <summary>
    ///     Reads the last value of a repeated output option. Encoder options may target the
    ///     single mapped video stream; muxer compliance must be set without a stream specifier.
    /// </summary>
    private static string? LastOptionValue(
        IReadOnlyList<string> arguments, string option, bool allowVideoSpecifier = false)
    {
        string? value = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var current = arguments[i]?.Trim() ?? "";
            if (allowVideoSpecifier && current.StartsWith(option + ":", StringComparison.OrdinalIgnoreCase))
            {
                var specifier = current[(option.Length + 1)..].ToLowerInvariant();
                if (specifier is not ("0" or "v" or "v:0")) continue;
                current = option;
            }
            if (!current.Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            value = ++i < arguments.Count ? arguments[i] : null;
        }
        return value;
    }

    /// <summary> Values that explicitly disable Dolby Vision or its x265 profile. </summary>
    private static bool IsDisabled(string value) =>
        value.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off";
}
