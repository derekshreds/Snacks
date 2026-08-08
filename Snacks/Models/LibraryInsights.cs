namespace Snacks.Models;

/// <summary>
///     Aggregate library composition served by <c>/api/library/insights</c> and
///     rendered as distribution bars on the Library Health page — what codecs the
///     library holds, at which resolutions, and how far processing has gotten.
/// </summary>
public sealed class LibraryInsights
{
    /// <summary> One labeled segment of a distribution (codec, resolution bucket, or status). </summary>
    public sealed record Slice(string Label, int Count, long Bytes);

    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public int HdrFiles { get; init; }
    public int MusicFiles { get; init; }
    public List<Slice> Codecs { get; init; } = new();
    public List<Slice> Resolutions { get; init; } = new();
    public List<Slice> Statuses { get; init; } = new();
}
