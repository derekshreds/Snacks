using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Aggregates the append-only <see cref="EncodeHistory"/> ledger by Advanced
///     Video profile so the settings panel can show measured reality next to the
///     impact forecast. Pure over its input rows; the repository supplies only
///     rows that carry an advanced profile label.
/// </summary>
public static class AdvancedVideoMeasuredService
{
    public sealed record ProfileMeasure
    {
        public Guid? ProfileId { get; init; }
        public required string ProfileName { get; init; }
        public int Jobs { get; init; }
        public int Kept { get; init; }
        public int Discarded { get; init; }
        public long OriginalBytes { get; init; }
        public long EncodedBytes { get; init; }
        public long BytesSaved { get; init; }

        /// <summary>Duration-weighted mean output bitrate over kept encodes, kb/s.</summary>
        public long? AvgEncodedKbps { get; init; }
    }

    public static List<ProfileMeasure> Aggregate(IEnumerable<EncodeHistory> rows)
    {
        return rows
            .Where(row => !string.IsNullOrEmpty(row.AdvancedProfileName))
            .GroupBy(row => row.AdvancedProfileName!)
            .Select(group =>
            {
                var kept = group.Where(row => row.EncodedSizeBytes > 0).ToList();
                var keptSeconds = kept.Sum(row => row.DurationSeconds);
                var keptBytes = kept.Sum(row => row.EncodedSizeBytes);
                return new ProfileMeasure
                {
                    // Ids can differ across renames that reused a name; keep the
                    // most recent so the UI links to a profile that still exists.
                    ProfileId     = group.OrderByDescending(row => row.CompletedAt).First().AdvancedProfileId,
                    ProfileName   = group.Key,
                    Jobs          = group.Count(),
                    Kept          = kept.Count,
                    Discarded     = group.Count() - kept.Count,
                    OriginalBytes = kept.Sum(row => row.OriginalSizeBytes),
                    EncodedBytes  = keptBytes,
                    BytesSaved    = group.Sum(row => row.BytesSaved),
                    AvgEncodedKbps = keptSeconds > 0
                        ? (long)(keptBytes * 8.0 / 1000.0 / keptSeconds)
                        : null,
                };
            })
            .OrderByDescending(measure => measure.Jobs)
            .ThenBy(measure => measure.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
