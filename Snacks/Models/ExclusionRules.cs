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

    /// <summary>
    ///     Maps a width/height pair to the nearest standard resolution label
    ///     (<c>2160p</c>, <c>1440p</c>, <c>1080p</c>, <c>720p</c>, <c>480p</c>) for comparison
    ///     against <see cref="ExcludeResolutions"/>. Returns <see langword="null"/> when either
    ///     dimension is non-positive. Buckets by height with a small tolerance to absorb
    ///     odd-aspect masters (e.g. 1920&#215;800).
    /// </summary>
    public static string? ClassifyResolution(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        // Classify by the larger of height / derived-4:3 from width so ultra-wide crops still
        // bucket with their native vertical resolution.
        int h = Math.Max(height, width * 3 / 4);
        if (h >= 2000) return "2160p";
        if (h >= 1300) return "1440p";
        if (h >= 900)  return "1080p";
        if (h >= 600)  return "720p";
        if (h >= 380)  return "480p";
        return null;
    }
}
