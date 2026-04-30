using FluentAssertions;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Overrides;

/// <summary>
///     <see cref="EncoderOptionsOverride.ApplyOverrides"/> covers a lot of nullable plumbing.
///     The tests below focus on three things that have actually broken in the past:
///     1) base options aren't mutated; 2) folder + node overrides layer in the right order;
///     3) the audio-fields special-case (legacy override → new shape) re-runs migration when needed.
/// </summary>
public sealed class EncoderOptionOverrideTests
{
    private static EncoderOptions BaseOptions() => new()
    {
        Format                = "mkv",
        TargetBitrate         = 3500,
        HardwareAcceleration  = "auto",
        PreserveOriginalAudio = true,
        AudioOutputs          = new(),
    };


    [Fact]
    public void Apply_with_no_overrides_is_a_clone()
    {
        var baseOpts = BaseOptions();
        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, null, null);

        result.Should().NotBeSameAs(baseOpts);
        result.Format.Should().Be(baseOpts.Format);
        result.TargetBitrate.Should().Be(baseOpts.TargetBitrate);
    }


    [Fact]
    public void Folder_override_layers_first_then_node_override_wins()
    {
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride { TargetBitrate = 5000, HardwareAcceleration = "intel" };
        var node     = new EncoderOptionsOverride { HardwareAcceleration = "nvidia" };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, node);

        result.TargetBitrate.Should().Be(5000);              // folder wins (node didn't set it)
        result.HardwareAcceleration.Should().Be("nvidia");   // node wins over folder
    }


    [Fact]
    public void Apply_does_not_mutate_base_options()
    {
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride { TargetBitrate = 9999 };

        EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        baseOpts.TargetBitrate.Should().Be(3500);
    }


    [Fact]
    public void Audio_outputs_override_replaces_base_list_entirely()
    {
        var baseOpts = BaseOptions();
        baseOpts.AudioOutputs.Add(new AudioOutputProfile { Codec = "aac", Layout = "Stereo" });
        baseOpts.AudioOutputs.Add(new AudioOutputProfile { Codec = "opus", Layout = "5.1" });

        var folder = new EncoderOptionsOverride
        {
            AudioOutputs = new()
            {
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1", BitrateKbps = 448 },
            }
        };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.AudioOutputs.Should().ContainSingle();
        result.AudioOutputs[0].Codec.Should().Be("ac3");
    }


    [Fact]
    public void Legacy_audio_override_alone_re_runs_migration_so_planner_sees_it()
    {
        // Base has Preserve=true, no outputs. A folder override that only sets the legacy
        // AudioCodec field would otherwise be invisible to the planner — Apply should detect
        // that the override is legacy-only and re-derive AudioOutputs from it.
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride
        {
            AudioCodec       = "aac",
            AudioBitrateKbps = 192,
            TwoChannelAudio  = true,
        };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.PreserveOriginalAudio.Should().BeFalse();
        result.AudioOutputs.Should().ContainSingle();
        result.AudioOutputs[0].Codec.Should().Be("aac");
        result.AudioOutputs[0].Layout.Should().Be("Stereo");
        result.AudioOutputs[0].BitrateKbps.Should().Be(192);
    }


    [Fact]
    public void New_audio_override_present_does_not_trigger_legacy_migration()
    {
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride
        {
            // Legacy fields present alongside the new shape — the new shape wins.
            AudioCodec   = "aac",
            AudioOutputs = new()
            {
                new AudioOutputProfile { Codec = "opus", Layout = "5.1" },
            }
        };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.AudioOutputs.Should().ContainSingle();
        result.AudioOutputs[0].Codec.Should().Be("opus");
    }


    [Fact]
    public void Empty_non_null_AudioOutputs_override_clears_base_list()
    {
        // Distinct from the `null` (no override) case: a non-null empty list explicitly
        // means "no encoded outputs" and must replace the base.
        var baseOpts = BaseOptions();
        baseOpts.AudioOutputs.Add(new AudioOutputProfile { Codec = "aac", Layout = "Stereo" });

        var folder = new EncoderOptionsOverride { AudioOutputs = new() };
        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Folder_new_shape_then_node_legacy_re_runs_migration()
    {
        // Two-layer scenario: folder configures the new shape, node configures the legacy
        // fields. Because the node override touches only legacy fields, the migration
        // re-runs on the post-folder result, replacing the folder's AudioOutputs.
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride
        {
            AudioOutputs = new() { new AudioOutputProfile { Codec = "opus", Layout = "5.1" } },
        };
        var node = new EncoderOptionsOverride
        {
            AudioCodec       = "aac",
            AudioBitrateKbps = 256,
            TwoChannelAudio  = true,
        };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, node);

        // Node layer's legacy fields fully replace the folder's AudioOutputs via re-migration.
        result.AudioOutputs.Should().ContainSingle();
        result.AudioOutputs[0].Codec.Should().Be("aac");
        result.AudioOutputs[0].Layout.Should().Be("Stereo");
        result.AudioOutputs[0].BitrateKbps.Should().Be(256);
        result.PreserveOriginalAudio.Should().BeFalse();
    }


    [Fact]
    public void Folder_legacy_then_node_new_shape_lets_node_win()
    {
        // The reverse: folder sets legacy fields (re-runs migration), then node provides
        // the new shape which replaces the folder's derived AudioOutputs.
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride
        {
            AudioCodec       = "aac",
            AudioBitrateKbps = 192,
            TwoChannelAudio  = true,
        };
        var node = new EncoderOptionsOverride
        {
            PreserveOriginalAudio = true,
            AudioOutputs = new() { new AudioOutputProfile { Codec = "opus", Layout = "5.1", BitrateKbps = 256 } },
        };

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, node);

        result.PreserveOriginalAudio.Should().BeTrue();
        result.AudioOutputs.Should().ContainSingle();
        result.AudioOutputs[0].Codec.Should().Be("opus");
    }


    /// <summary>
    ///     Full-field reflection matrix: every primitive override field on
    ///     <see cref="EncoderOptionsOverride"/> with a corresponding setter on
    ///     <see cref="EncoderOptions"/>, paired with a representative non-default value.
    ///     One test per field would be 30+ near-identical methods; the data row keeps
    ///     coverage high without the copy-paste.
    /// </summary>
    public static IEnumerable<object[]> SimpleOverrideRows() => new[]
    {
        // Core video
        new object[] { "Format",                "mp4"     },
        new object[] { "Codec",                 "h264"    },
        new object[] { "Encoder",               "libx264" },
        new object[] { "TargetBitrate",         8000      },
        new object[] { "StrictBitrate",         true      },
        new object[] { "FourKBitrateMultiplier", 6        },
        new object[] { "Skip4K",                true      },
        new object[] { "HardwareAcceleration",  "nvidia"  },
        new object[] { "SkipPercentAboveTarget", 30       },
        new object[] { "FfmpegQualityPreset",   "slow"    },

        // Audio (legacy + originalLanguageProvider)
        new object[] { "OriginalLanguageProvider", "Sonarr" },
        new object[] { "KeepOriginalLanguage",     true     },

        // Encoding mode
        new object[] { "EncodingMode",          EncodingMode.MuxOnly },
        new object[] { "MuxStreams",            MuxStreams.Audio     },

        // Subtitles
        new object[] { "ExtractSubtitlesToSidecar",    true   },
        new object[] { "SidecarSubtitleFormat",        "ass"  },
        new object[] { "ConvertImageSubtitlesToSrt",   true   },
        new object[] { "PassThroughImageSubtitlesMkv", true   },

        // Video pipeline
        new object[] { "DownscalePolicy",       "Always"  },
        new object[] { "DownscaleTarget",       "720p"    },
        new object[] { "TonemapHdrToSdr",       true      },
        new object[] { "RemoveBlackBorders",    true      },

        // Output / scratch
        new object[] { "RetryOnFail",           false     },
        new object[] { "DeleteOriginalFile",    true      },
        new object[] { "OutputDirectory",       "/tmp/out"     },
        new object[] { "EncodeDirectory",       "/tmp/encode"  },
    };

    [Theory]
    [MemberData(nameof(SimpleOverrideRows))]
    public void Single_field_override_lands_on_result(string fieldName, object value)
    {
        var baseOpts = BaseOptions();
        var folder   = new EncoderOptionsOverride();

        // Reflection keeps the test compact: the override types mirror the EncoderOptions
        // shape exactly, so we set both sides via the same field name.
        var overrideField = typeof(EncoderOptionsOverride).GetProperty(fieldName)!;
        overrideField.SetValue(folder, value);

        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        var resultField = typeof(EncoderOptions).GetProperty(fieldName)!;
        resultField.GetValue(result).Should().Be(value);
    }


    [Fact]
    public void PreserveOriginalAudio_override_lands_on_result()
    {
        // Boolean defaulting-to-true can't go through the reflection matrix (the override
        // value matches the base default), so we test it explicitly with the off case.
        var baseOpts = BaseOptions();
        baseOpts.PreserveOriginalAudio.Should().BeTrue();

        var folder = new EncoderOptionsOverride { PreserveOriginalAudio = false };
        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.PreserveOriginalAudio.Should().BeFalse();
    }


    [Fact]
    public void AudioLanguagesToKeep_override_replaces_base_list()
    {
        var baseOpts = BaseOptions();
        baseOpts.AudioLanguagesToKeep = new() { "en" };

        var folder = new EncoderOptionsOverride { AudioLanguagesToKeep = new() { "ja", "fr" } };
        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.AudioLanguagesToKeep.Should().BeEquivalentTo(new[] { "ja", "fr" });
    }


    [Fact]
    public void SubtitleLanguagesToKeep_override_replaces_base_list()
    {
        var baseOpts = BaseOptions();
        baseOpts.SubtitleLanguagesToKeep = new() { "en" };

        var folder = new EncoderOptionsOverride { SubtitleLanguagesToKeep = new() { "en", "ja" } };
        var result = EncoderOptionsOverride.ApplyOverrides(baseOpts, folder, null);

        result.SubtitleLanguagesToKeep.Should().BeEquivalentTo(new[] { "en", "ja" });
    }


    // =====================================================================
    //  Clone deep-copy. Critical for AudioOutputs and the language lists —
    //  per-job mutation must not bleed back into the shared base options.
    // =====================================================================

    [Fact]
    public void Clone_does_not_share_AudioOutputs_list_or_profiles()
    {
        var original = BaseOptions();
        original.AudioOutputs.Add(new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 });

        var clone = original.Clone();

        clone.AudioOutputs.Should().NotBeSameAs(original.AudioOutputs);
        clone.AudioOutputs[0].Should().NotBeSameAs(original.AudioOutputs[0]);

        // Mutating the clone's profile must not reach back to the original.
        clone.AudioOutputs[0].Codec = "opus";
        original.AudioOutputs[0].Codec.Should().Be("aac");

        // Mutating the clone's list shape must not reach back either.
        clone.AudioOutputs.Add(new AudioOutputProfile { Codec = "ac3", Layout = "5.1" });
        original.AudioOutputs.Should().ContainSingle();
    }


    [Fact]
    public void Clone_does_not_share_language_lists()
    {
        var original = BaseOptions();
        original.AudioLanguagesToKeep    = new() { "en" };
        original.SubtitleLanguagesToKeep = new() { "en" };

        var clone = original.Clone();
        clone.AudioLanguagesToKeep.Add("ja");
        clone.SubtitleLanguagesToKeep.Add("fr");

        original.AudioLanguagesToKeep.Should().BeEquivalentTo(new[] { "en" });
        original.SubtitleLanguagesToKeep.Should().BeEquivalentTo(new[] { "en" });
    }
}
