namespace Snacks.Models;

/// <summary>
///     Auto-scan exclusion criteria. Files matching any rule are skipped during scans.
///     Persisted as a sub-object inside <see cref="AutoScanConfig"/>.
/// </summary>
public sealed class ExclusionRules
{
    /// <summary> Filename glob patterns (e.g. "*REMUX*", "*-BLURAY-*"). </summary>
    public List<string> FilenamePatterns { get; set; } = new();

    /// <summary> When set, skip files larger than this size in gigabytes. </summary>
    public double? MinSizeGBToSkip { get; set; }

    /// <summary> Resolution labels to skip (e.g. "2160p", "1080p", "720p", "480p"). </summary>
    public List<string> ExcludeResolutions { get; set; } = new();

    /// <summary>
    ///     Returns <see langword="true"/> when the given filename, size, or resolution label
    ///     matches any exclusion rule.
    /// </summary>
    /// <param name="filename"> The file name to test against glob patterns. </param>
    /// <param name="sizeBytes"> The file size in bytes, used for size-threshold checks. </param>
    /// <param name="resolutionLabel"> The resolution label (e.g. "1080p") to test against excluded resolutions. </param>
    public bool IsExcluded(string filename, long? sizeBytes, string? resolutionLabel)
    {
        foreach (var pattern in FilenamePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (GlobMatch(filename, pattern)) return true;
        }

        if (MinSizeGBToSkip.HasValue && sizeBytes.HasValue)
        {
            var gb = sizeBytes.Value / (1024d * 1024d * 1024d);
            if (gb >= MinSizeGBToSkip.Value) return true;
        }

        if (!string.IsNullOrEmpty(resolutionLabel)
            && ExcludeResolutions.Any(r => string.Equals(r, resolutionLabel, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static bool GlobMatch(string input, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            input, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
