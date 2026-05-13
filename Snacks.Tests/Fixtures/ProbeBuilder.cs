using Snacks.Models;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     Fluent builder for <see cref="ProbeResult"/> fixtures used in flag-generation tests.
///     Auto-assigns stream indices in declaration order so tests don't have to count.
/// </summary>
internal sealed class ProbeBuilder
{
    private readonly List<Stream> _streams = new();

    public ProbeBuilder Video(
        string  codec         = "h264",
        string? colorTransfer = null,
        int     width         = 1920,
        int     height        = 1080)
    {
        _streams.Add(new Stream
        {
            Index         = _streams.Count,
            CodecType     = "video",
            CodecName     = codec,
            ColorTransfer = colorTransfer,
            Width         = width,
            Height        = height,
        });
        return this;
    }

    public ProbeBuilder Audio(
        string codec    = "aac",
        int    channels = 2,
        string lang     = "eng",
        string? title   = null)
    {
        _streams.Add(new Stream
        {
            Index         = _streams.Count,
            CodecType     = "audio",
            CodecName     = codec,
            Channels      = channels,
            ChannelLayout = ChannelLayoutFor(channels),
            Tags          = new Tags { Language = lang, Title = title },
        });
        return this;
    }

    public ProbeBuilder Subtitle(
        string codec           = "subrip",
        string lang            = "eng",
        string? title          = null,
        bool   hearingImpaired = false,
        bool   defaultFlag     = false,
        bool   forced          = false)
    {
        _streams.Add(new Stream
        {
            Index       = _streams.Count,
            CodecType   = "subtitle",
            CodecName   = codec,
            Tags        = new Tags { Language = lang, Title = title },
            Disposition = (hearingImpaired || defaultFlag || forced)
                ? new Disposition
                  {
                      HearingImpaired = hearingImpaired ? 1 : 0,
                      Default         = defaultFlag     ? 1 : 0,
                      Forced          = forced          ? 1 : 0,
                  }
                : null,
        });
        return this;
    }

    public ProbeResult Build() => new() { Streams = _streams.ToArray() };

    private static string ChannelLayoutFor(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}c",
    };
}
