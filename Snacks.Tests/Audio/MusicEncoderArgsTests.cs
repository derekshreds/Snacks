using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Audio;

/// <summary>
///     Rate control in the music argument builder: <c>VbrQuality</c> becomes <c>-q:a</c> for
///     encoders with a quality scale, while Opus — which has none, and rejects <c>-q:a</c> —
///     always encodes VBR toward the configured bitrate.
/// </summary>
public sealed class MusicEncoderArgsTests
{
    private static IReadOnlyList<string> Build(string codec, int? vbrQuality, int bitrate = 160) =>
        MusicEncoderArgs.Build("/in.flac", "/out [snacks].x",
            new MusicEncoderOptions { Codec = codec, VbrQuality = vbrQuality, BitrateKbps = bitrate },
            new ProbeBuilder().Audio(codec: "flac").Build()).Arguments;

    [Fact]
    public void Opus_ignores_vbr_quality_and_targets_the_bitrate()
    {
        var args = Build("libopus", vbrQuality: 5);

        args.Should().NotContain("-q:a");
        args.Should().ContainInOrder("-c:a", "libopus", "-b:a", "160k");
    }

    [Theory]
    [InlineData("libmp3lame")]
    [InlineData("libvorbis")]
    [InlineData("aac")]
    public void Other_lossy_encoders_keep_using_vbr_quality(string codec)
    {
        var args = Build(codec, vbrQuality: 2);

        args.Should().ContainInOrder("-q:a", "2");
        args.Should().NotContain("-b:a");
    }

    [Theory]
    [InlineData("libopus")]
    [InlineData("libmp3lame")]
    public void Without_vbr_quality_every_lossy_encoder_uses_the_bitrate(string codec)
    {
        Build(codec, vbrQuality: null).Should().ContainInOrder("-b:a", "160k");
    }
}
