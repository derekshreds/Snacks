using Snacks.Models;
using Snacks.Services;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     Fluent builder for <see cref="ProbeResult"/> fixtures used in flag-generation tests.
///     Auto-assigns stream indices in declaration order so tests don't have to count.
/// </summary>
internal sealed class ProbeBuilder
{
    private readonly List<Stream> _streams = new();

    /// <summary> Adds a video stream, optionally with a Dolby Vision record. </summary>
    public ProbeBuilder Video(
        string  codec              = "h264",
        string? colorTransfer      = null,
        int     width              = 1920,
        int     height             = 1080,
        string? frameRate          = null,
        string? codecTag           = null,
        int?    dolbyVisionProfile = null)
    {
        _streams.Add(new Stream
        {
            Index          = _streams.Count,
            CodecType      = "video",
            CodecName      = codec,
            CodecTagString = codecTag,
            ColorTransfer  = colorTransfer,
            Width          = width,
            Height         = height,
            AvgFrameRate   = frameRate,
            RFrameRate     = frameRate,
            SideDataList   = DolbyVisionSideData(dolbyVisionProfile),
        });
        return this;
    }

    /// <summary> Adds an audio stream; the channel layout follows the channel count. </summary>
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

    /// <summary> Adds a subtitle stream with optional disposition flags. </summary>
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

    /// <summary> Materialises the probe result with the streams added so far. </summary>
    public ProbeResult Build() => new() { Streams = _streams.ToArray() };

    private static IReadOnlyList<StreamSideData>? DolbyVisionSideData(int? profile)
    {
        if (profile == null) return null;

        return [new StreamSideData
        {
            SideDataType = Mp4SampleEntryOptions.DolbyVisionSideDataType,
            DvProfile    = profile.Value,
        }];
    }

    private static string ChannelLayoutFor(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}c",
    };
}
