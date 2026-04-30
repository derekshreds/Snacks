using FluentAssertions;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Audio;

/// <summary>
///     Round-trip tests for the legacy <see cref="EncoderOptions.AudioCodec"/> +
///     <see cref="EncoderOptions.AudioBitrateKbps"/> + <see cref="EncoderOptions.TwoChannelAudio"/>
///     trio → new <see cref="EncoderOptions.PreserveOriginalAudio"/> +
///     <see cref="EncoderOptions.AudioOutputs"/> shape.
/// </summary>
public sealed class AudioMigrationTests
{
    /// <summary>Rows: (legacy codec, downmix flag, legacy bitrate, expected preserve, expected output count, expected codec, expected layout, expected bitrate).</summary>
    public static IEnumerable<object?[]> MigrationRows() => new[]
    {
        new object?[] { "copy", false, 192, true,  0, null,  null,     0   },
        new object?[] { "copy", true,  192, true,  0, null,  null,     0   },   // copy ignores downmix flag
        new object?[] { "aac",  false, 192, false, 1, "aac", "Source", 192 },
        new object?[] { "aac",  true,  192, false, 1, "aac", "Stereo", 192 },
        new object?[] { "eac3", false, 384, false, 1, "eac3", "Source", 384 },
        new object?[] { "opus", true,  160, false, 1, "opus", "Stereo", 160 },
        new object?[] { "",     false, 192, true,  0, null,  null,     0   },   // empty string treated as copy
    };

    [Theory]
    [MemberData(nameof(MigrationRows))]
    public void Legacy_audio_fields_translate_to_new_shape(
        string  legacyCodec,
        bool    legacyDownmix,
        int     legacyBitrate,
        bool    expectedPreserve,
        int     expectedOutputCount,
        string? expectedCodec,
        string? expectedLayout,
        int     expectedBitrate)
    {
        var opts = new EncoderOptions
        {
            AudioCodec       = legacyCodec,
            TwoChannelAudio  = legacyDownmix,
            AudioBitrateKbps = legacyBitrate,
            AudioOutputs     = new(),
        };

        opts.ApplyLegacyAudioMigration();

        opts.PreserveOriginalAudio.Should().Be(expectedPreserve);
        opts.AudioOutputs.Should().HaveCount(expectedOutputCount);

        if (expectedOutputCount > 0)
        {
            opts.AudioOutputs[0].Codec.Should().Be(expectedCodec);
            opts.AudioOutputs[0].Layout.Should().Be(expectedLayout);
            opts.AudioOutputs[0].BitrateKbps.Should().Be(expectedBitrate);
        }
    }


    [Fact]
    public void Migration_is_idempotent_when_new_shape_already_populated()
    {
        var opts = new EncoderOptions
        {
            AudioCodec            = "aac",
            TwoChannelAudio       = true,
            AudioBitrateKbps      = 192,
            PreserveOriginalAudio = true,
            AudioOutputs          = new()
            {
                new AudioOutputProfile { Codec = "opus", Layout = "5.1", BitrateKbps = 256 },
            },
        };

        opts.ApplyLegacyAudioMigration();

        // Pre-populated AudioOutputs are left alone — migration only fills empty configs.
        opts.PreserveOriginalAudio.Should().BeTrue();
        opts.AudioOutputs.Should().ContainSingle();
        opts.AudioOutputs[0].Codec.Should().Be("opus");
    }


    [Fact]
    public void Default_options_migrate_to_preserve_only()
    {
        var opts = new EncoderOptions();
        opts.ApplyLegacyAudioMigration();

        opts.PreserveOriginalAudio.Should().BeTrue();
        opts.AudioOutputs.Should().BeEmpty();
    }
}
