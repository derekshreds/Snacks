using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     Minimal per-track summary persisted alongside each <see cref="MediaFile" />
///     so mux-pass eligibility can be re-evaluated without re-running ffprobe.
///     JSON keys are single-letter to keep row footprint small
///     (<c>l</c>=language, <c>c</c>=codec, <c>ch</c>=channels).
/// </summary>
public sealed class AudioStreamSummary
{
    [JsonPropertyName("l")]  public string? Language  { get; set; }
    [JsonPropertyName("c")]  public string? CodecName { get; set; }
    [JsonPropertyName("ch")] public int     Channels  { get; set; }
}

/// <summary> Minimal subtitle-track summary. See <see cref="AudioStreamSummary" />. </summary>
public sealed class SubtitleStreamSummary
{
    [JsonPropertyName("l")] public string? Language  { get; set; }
    [JsonPropertyName("c")] public string? CodecName { get; set; }
}

/// <summary>
///     JSON (de)serialization helpers for the compact stream summaries stored on
///     <see cref="MediaFile.AudioStreams" /> and <see cref="MediaFile.SubtitleStreams" />.
/// </summary>
public static class MuxStreamSummary
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IEnumerable<AudioStreamSummary> streams) =>
        JsonSerializer.Serialize(streams, _opts);

    public static string Serialize(IEnumerable<SubtitleStreamSummary> streams) =>
        JsonSerializer.Serialize(streams, _opts);

    public static IReadOnlyList<AudioStreamSummary> DeserializeAudio(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<AudioStreamSummary>();
        try   { return JsonSerializer.Deserialize<List<AudioStreamSummary>>(json, _opts) ?? new(); }
        catch { return Array.Empty<AudioStreamSummary>(); }
    }

    public static IReadOnlyList<SubtitleStreamSummary> DeserializeSubtitle(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<SubtitleStreamSummary>();
        try   { return JsonSerializer.Deserialize<List<SubtitleStreamSummary>>(json, _opts) ?? new(); }
        catch { return Array.Empty<SubtitleStreamSummary>(); }
    }
}
