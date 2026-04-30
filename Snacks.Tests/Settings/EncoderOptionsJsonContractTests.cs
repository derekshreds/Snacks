using System.Text.Json;
using FluentAssertions;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     The JSON shape produced by serializing <see cref="EncoderOptions"/> is the wire
///     contract between the .NET backend and the Razor/JS frontend. The frontend's
///     <c>encoder-form.js</c> reads property names directly (with camelCase fallback),
///     so renaming a field, changing an enum encoding, or omitting one breaks the UI
///     silently. These tests pin the contract: top-level property names, the legacy
///     audio fields, the new audio shape, and the round-trip migration path used by
///     <c>SettingsController</c>.
/// </summary>
public sealed class EncoderOptionsJsonContractTests
{
    /// <summary>
    ///     Match the serializer config in <c>SettingsController._jsonOptions</c>:
    ///     case-insensitive read, indented write. Tests must use the same options so
    ///     a contract regression in real usage shows up here.
    /// </summary>
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented            = true,
        PropertyNameCaseInsensitive = true,
    };


    // =====================================================================
    //  Default-options serialization shape — top-level property names that
    //  the JS form reader and the override mirror both depend on.
    // =====================================================================

    [Theory]
    [InlineData("Format")]
    [InlineData("Codec")]
    [InlineData("Encoder")]
    [InlineData("TargetBitrate")]
    [InlineData("StrictBitrate")]
    [InlineData("FourKBitrateMultiplier")]
    [InlineData("Skip4K")]
    [InlineData("FfmpegQualityPreset")]
    [InlineData("HardwareAcceleration")]
    [InlineData("SkipPercentAboveTarget")]
    // Audio — both the legacy fields (still serialized for back-compat read)
    // and the new shape used by the planner.
    [InlineData("AudioLanguagesToKeep")]
    [InlineData("KeepOriginalLanguage")]
    [InlineData("OriginalLanguageProvider")]
    [InlineData("AudioCodec")]
    [InlineData("AudioBitrateKbps")]
    [InlineData("TwoChannelAudio")]
    [InlineData("PreserveOriginalAudio")]
    [InlineData("AudioOutputs")]
    // Encoding mode
    [InlineData("EncodingMode")]
    [InlineData("MuxStreams")]
    // Subtitles
    [InlineData("SubtitleLanguagesToKeep")]
    [InlineData("ExtractSubtitlesToSidecar")]
    [InlineData("SidecarSubtitleFormat")]
    [InlineData("ConvertImageSubtitlesToSrt")]
    [InlineData("PassThroughImageSubtitlesMkv")]
    // Video pipeline
    [InlineData("DownscalePolicy")]
    [InlineData("DownscaleTarget")]
    [InlineData("TonemapHdrToSdr")]
    [InlineData("RemoveBlackBorders")]
    // Output / scratch
    [InlineData("DeleteOriginalFile")]
    [InlineData("RetryOnFail")]
    public void Serialized_default_options_contain_expected_top_level_property(string propertyName)
    {
        var json = JsonSerializer.Serialize(new EncoderOptions(), Opts);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty(propertyName, out _).Should().BeTrue(
            $"the JS frontend reads `{propertyName}` from the saved settings");
    }


    // =====================================================================
    //  Enum encoding — both EncodingMode and MuxStreams use
    //  JsonStringEnumConverter, so they serialize as strings, not numbers.
    //  The frontend `encoder-form.js` writes/reads the string forms.
    // =====================================================================

    [Theory]
    [InlineData(EncodingMode.Transcode, "Transcode")]
    [InlineData(EncodingMode.Hybrid,    "Hybrid")]
    [InlineData(EncodingMode.MuxOnly,   "MuxOnly")]
    public void EncodingMode_serializes_as_string(EncodingMode mode, string expected)
    {
        var opts = new EncoderOptions { EncodingMode = mode };
        var json = JsonSerializer.Serialize(opts, Opts);

        json.Should().Contain($"\"EncodingMode\": \"{expected}\"");
    }


    [Theory]
    [InlineData(MuxStreams.Both,      "Both")]
    [InlineData(MuxStreams.Audio,     "Audio")]
    [InlineData(MuxStreams.Subtitles, "Subtitles")]
    public void MuxStreams_serializes_as_string(MuxStreams mux, string expected)
    {
        var opts = new EncoderOptions { MuxStreams = mux };
        var json = JsonSerializer.Serialize(opts, Opts);

        json.Should().Contain($"\"MuxStreams\": \"{expected}\"");
    }


    // =====================================================================
    //  AudioOutputs nested shape — each entry has Codec / Layout / BitrateKbps.
    // =====================================================================

    [Fact]
    public void AudioOutputs_round_trip_preserves_shape()
    {
        var original = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs = new()
            {
                new AudioOutputProfile { Codec = "aac",  Layout = "Stereo", BitrateKbps = 192 },
                new AudioOutputProfile { Codec = "opus", Layout = "5.1",    BitrateKbps = 256 },
            },
        };

        var json   = JsonSerializer.Serialize(original, Opts);
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, Opts);

        parsed.Should().NotBeNull();
        parsed!.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().HaveCount(2);
        parsed.AudioOutputs[0].Codec.Should().Be("aac");
        parsed.AudioOutputs[0].Layout.Should().Be("Stereo");
        parsed.AudioOutputs[0].BitrateKbps.Should().Be(192);
        parsed.AudioOutputs[1].Codec.Should().Be("opus");
        parsed.AudioOutputs[1].Layout.Should().Be("5.1");
        parsed.AudioOutputs[1].BitrateKbps.Should().Be(256);
    }


    [Fact]
    public void AudioOutputProfile_property_names_are_PascalCase()
    {
        // JS reads "Codec" / "Layout" / "BitrateKbps". Renaming a property in the
        // .NET model would break `readAudioOutputs` in encoder-form.js.
        var json = JsonSerializer.Serialize(
            new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
            Opts);

        json.Should().Contain("\"Codec\"");
        json.Should().Contain("\"Layout\"");
        json.Should().Contain("\"BitrateKbps\"");
    }


    // =====================================================================
    //  Legacy → new-shape migration via JSON round trip — the actual path
    //  the SettingsController.Get exercises on every read.
    // =====================================================================

    [Fact]
    public void Legacy_settings_json_migrates_to_new_audio_shape_on_read()
    {
        // What an old settings.json file looks like — only legacy audio fields, no new shape.
        const string legacyJson = """
            {
                "Format": "mkv",
                "Codec": "h265",
                "Encoder": "libx265",
                "TargetBitrate": 3500,
                "AudioCodec": "aac",
                "AudioBitrateKbps": 192,
                "TwoChannelAudio": true
            }
            """;

        var parsed = JsonSerializer.Deserialize<EncoderOptions>(legacyJson, Opts);
        parsed.Should().NotBeNull();
        parsed!.ApplyLegacyAudioMigration();

        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().ContainSingle();
        parsed.AudioOutputs[0].Codec.Should().Be("aac");
        parsed.AudioOutputs[0].Layout.Should().Be("Stereo");
        parsed.AudioOutputs[0].BitrateKbps.Should().Be(192);
    }


    [Fact]
    public void Legacy_copy_codec_migrates_to_preserve_only()
    {
        const string legacyJson = """
            {
                "Format": "mkv",
                "AudioCodec": "copy",
                "AudioBitrateKbps": 192,
                "TwoChannelAudio": false
            }
            """;

        var parsed = JsonSerializer.Deserialize<EncoderOptions>(legacyJson, Opts);
        parsed!.ApplyLegacyAudioMigration();

        parsed.PreserveOriginalAudio.Should().BeTrue();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Json_with_only_new_audio_shape_round_trips_without_modification()
    {
        const string newShapeJson = """
            {
                "PreserveOriginalAudio": false,
                "AudioOutputs": [
                    { "Codec": "ac3", "Layout": "5.1", "BitrateKbps": 448 }
                ]
            }
            """;

        var parsed = JsonSerializer.Deserialize<EncoderOptions>(newShapeJson, Opts);
        parsed!.ApplyLegacyAudioMigration();   // idempotent — should not touch AudioOutputs

        parsed.AudioOutputs.Should().ContainSingle();
        parsed.AudioOutputs[0].Codec.Should().Be("ac3");
        parsed.AudioOutputs[0].Layout.Should().Be("5.1");
        parsed.AudioOutputs[0].BitrateKbps.Should().Be(448);
        parsed.PreserveOriginalAudio.Should().BeFalse();
    }


    // =====================================================================
    //  Case-insensitive deserialization — JS may emit camelCase property
    //  names (the form reader looks at both Pascal and camel for safety).
    // =====================================================================

    [Fact]
    public void Deserialization_is_case_insensitive_for_PascalCase_or_camelCase_input()
    {
        const string camelJson = """
            {
                "format": "mp4",
                "targetBitrate": 5000,
                "preserveOriginalAudio": false,
                "audioOutputs": [
                    { "codec": "aac", "layout": "Stereo", "bitrateKbps": 128 }
                ]
            }
            """;

        var parsed = JsonSerializer.Deserialize<EncoderOptions>(camelJson, Opts);

        parsed!.Format.Should().Be("mp4");
        parsed.TargetBitrate.Should().Be(5000);
        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().ContainSingle();
        parsed.AudioOutputs[0].Codec.Should().Be("aac");
    }


    // =====================================================================
    //  Empty / partial JSON falls back to defaults rather than throwing —
    //  the SettingsController's first-load path depends on this.
    // =====================================================================

    [Fact]
    public void Empty_object_json_deserializes_to_defaults()
    {
        var parsed = JsonSerializer.Deserialize<EncoderOptions>("{}", Opts);
        parsed.Should().NotBeNull();
        parsed!.Format.Should().Be("mkv");
        parsed.TargetBitrate.Should().Be(3500);
        parsed.PreserveOriginalAudio.Should().BeTrue();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    // =====================================================================
    //  Auto-population on legacy → new-shape upgrade. The full sequence the
    //  UI relies on:
    //    1. User had {AudioCodec=aac, TwoChannelAudio=true, AudioBitrateKbps=192}
    //    2. SettingsController.Get reads the file, calls ApplyLegacyAudioMigration.
    //    3. The serialized response carries the migrated AudioOutputs.
    //    4. encoder-form.js setAudioOutputs renders one row per profile.
    //  This pinning test catches a regression that breaks the auto-population
    //  the user expects after upgrade — e.g., the migration getting skipped,
    //  the layout mapping flipping, or the bitrate not flowing through.
    // =====================================================================

    public static IEnumerable<object?[]> AutoPopulationRows() => new[]
    {
        // (legacyJson, expectedPreserve, expectedRowCount, expectedCodec, expectedLayout, expectedBitrate)
        new object?[]
        {
            """{"AudioCodec":"aac","TwoChannelAudio":true,"AudioBitrateKbps":192}""",
            false, 1, "aac",  "Stereo", 192,
        },
        new object?[]
        {
            """{"AudioCodec":"aac","TwoChannelAudio":false,"AudioBitrateKbps":256}""",
            false, 1, "aac",  "Source", 256,
        },
        new object?[]
        {
            """{"AudioCodec":"eac3","TwoChannelAudio":false,"AudioBitrateKbps":384}""",
            false, 1, "eac3", "Source", 384,
        },
        new object?[]
        {
            """{"AudioCodec":"opus","TwoChannelAudio":true,"AudioBitrateKbps":160}""",
            false, 1, "opus", "Stereo", 160,
        },
        new object?[]
        {
            """{"AudioCodec":"copy"}""",
            true, 0, null, null, 0,
        },
        new object?[]
        {
            // Empty/missing AudioCodec is treated as copy.
            """{"Format":"mkv"}""",
            true, 0, null, null, 0,
        },
    };

    [Theory]
    [MemberData(nameof(AutoPopulationRows))]
    public void Legacy_settings_auto_populate_into_new_shape_for_UI_restoration(
        string  legacyJson,
        bool    expectedPreserve,
        int     expectedRowCount,
        string? expectedCodec,
        string? expectedLayout,
        int     expectedBitrate)
    {
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(legacyJson, Opts);
        parsed.Should().NotBeNull();
        parsed!.ApplyLegacyAudioMigration();

        // Re-serialize so we exercise the wire shape the JS reader actually consumes.
        var rehydrated = JsonSerializer.Deserialize<EncoderOptions>(
            JsonSerializer.Serialize(parsed, Opts), Opts);
        rehydrated.Should().NotBeNull();

        rehydrated!.PreserveOriginalAudio.Should().Be(expectedPreserve);
        rehydrated.AudioOutputs.Should().HaveCount(expectedRowCount);

        if (expectedRowCount > 0)
        {
            rehydrated.AudioOutputs[0].Codec.Should().Be(expectedCodec);
            rehydrated.AudioOutputs[0].Layout.Should().Be(expectedLayout);
            rehydrated.AudioOutputs[0].BitrateKbps.Should().Be(expectedBitrate);
        }
    }
}
