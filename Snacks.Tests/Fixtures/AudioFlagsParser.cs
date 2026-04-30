using System.Text.RegularExpressions;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     Parses the planner's audio-flag output into a structured per-output-stream view.
///     Tests assert against properties of <see cref="OutputStream"/> instead of comparing
///     whole strings — that way reordered or whitespace-different output still passes when
///     the FFmpeg semantics are equivalent.
/// </summary>
internal static class AudioFlagsParser
{
    /// <summary>One element per emitted output audio stream, in output-index order.</summary>
    internal sealed record OutputStream(
        int     OutputIndex,
        int     SourceIndex,
        string  Codec,
        int?    BitrateKbps,
        int?    Channels,
        bool    OpusVbr);

    private static readonly Regex MapRegex = new(@"-map\s+0:(?<src>\d+)", RegexOptions.Compiled);
    private static readonly Regex CodecRegex = new(@"-c:a:(?<idx>\d+)\s+(?<codec>\S+)", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"-b:a:(?<idx>\d+)\s+(?<br>\d+)k", RegexOptions.Compiled);
    private static readonly Regex ChannelsRegex = new(@"-ac:a:(?<idx>\d+)\s+(?<ch>\d+)", RegexOptions.Compiled);
    private static readonly Regex VbrRegex = new(@"-vbr:a:(?<idx>\d+)\s+on", RegexOptions.Compiled);

    /// <summary>
    ///     Parses the raw flag string emitted by <c>FfprobeService.MapAudio</c>.
    ///     Returns one <see cref="OutputStream"/> per <c>-c:a:N</c> token, gathering
    ///     the matching <c>-map 0:M</c> in declaration order.
    /// </summary>
    internal static IReadOnlyList<OutputStream> Parse(string flags)
    {
        if (string.IsNullOrWhiteSpace(flags)) return Array.Empty<OutputStream>();

        // -map tokens are emitted before -c:a tokens (planner writes maps first, codecs second).
        // We pair them positionally: the Nth -map corresponds to output index N.
        var sources = MapRegex.Matches(flags)
            .Select(m => int.Parse(m.Groups["src"].Value))
            .ToArray();

        var codecsByIdx   = CodecRegex.Matches(flags)
            .ToDictionary(m => int.Parse(m.Groups["idx"].Value), m => m.Groups["codec"].Value);
        var bitrateByIdx  = BitrateRegex.Matches(flags)
            .ToDictionary(m => int.Parse(m.Groups["idx"].Value), m => int.Parse(m.Groups["br"].Value));
        var channelsByIdx = ChannelsRegex.Matches(flags)
            .ToDictionary(m => int.Parse(m.Groups["idx"].Value), m => int.Parse(m.Groups["ch"].Value));
        var vbrByIdx      = VbrRegex.Matches(flags)
            .Select(m => int.Parse(m.Groups["idx"].Value))
            .ToHashSet();

        var streams = new List<OutputStream>();
        foreach (var idx in codecsByIdx.Keys.OrderBy(k => k))
        {
            streams.Add(new OutputStream(
                OutputIndex: idx,
                SourceIndex: idx < sources.Length ? sources[idx] : -1,
                Codec:       codecsByIdx[idx],
                BitrateKbps: bitrateByIdx.TryGetValue(idx, out var br) ? br : null,
                Channels:    channelsByIdx.TryGetValue(idx, out var ch) ? ch : null,
                OpusVbr:     vbrByIdx.Contains(idx)));
        }
        return streams;
    }
}
