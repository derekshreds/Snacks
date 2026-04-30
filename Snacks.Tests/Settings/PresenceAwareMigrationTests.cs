using System.Text.Json;
using FluentAssertions;
using Snacks.Controllers;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Regression suite for the "AAC keeps coming back when I delete all rows" bug.
///
///     Root cause: <see cref="EncoderOptions.ApplyLegacyAudioMigration"/> ran on every
///     load, and was triggered whenever <c>AudioOutputs</c> happened to be empty. Even
///     when the user explicitly saved <c>{ AudioOutputs: [] }</c> with the new form,
///     the migration would see the empty list, look at <c>AudioCodec</c> (which
///     defaults to <c>"copy"</c> after deserialize when the legacy fields are absent),
///     and overwrite <c>PreserveOriginalAudio</c> back to <c>true</c>. For users whose
///     historical settings still had a non-copy <c>AudioCodec</c> in the file, the
///     migration would re-add an AAC row each load.
///
///     The fix: the controller now gates migration on a presence-check —
///     <see cref="SettingsController.HasNewAudioShape"/> looks for either
///     <c>PreserveOriginalAudio</c> or <c>AudioOutputs</c> in the raw JSON. If either
///     is there, migration does not run, period. The user's saved state is the source
///     of truth from then on.
/// </summary>
public sealed class PresenceAwareMigrationTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };


    // =====================================================================
    //  HasNewAudioShape — the presence detector. Drives the migration gate.
    // =====================================================================

    /// <summary>Rows: (raw JSON, expected detection).</summary>
    public static IEnumerable<object[]> ShapeDetectionRows() => new[]
    {
        // Truly legacy (pre-update) — no new keys present.
        new object[] { """{"Format":"mkv","AudioCodec":"aac","TwoChannelAudio":true}""", false },
        new object[] { """{"AudioCodec":"copy","AudioBitrateKbps":192}""",                false },
        new object[] { """{"Format":"mkv"}""",                                            false },
        new object[] { """{}""",                                                          false },

        // New shape — either key alone is sufficient to count as "post-migration".
        new object[] { """{"AudioOutputs":[]}""",                                         true  },
        new object[] { """{"AudioOutputs":[{"Codec":"aac","Layout":"Stereo","BitrateKbps":192}]}""", true },
        new object[] { """{"PreserveOriginalAudio":true}""",                              true  },
        new object[] { """{"PreserveOriginalAudio":false,"AudioOutputs":[]}""",           true  },

        // Case-insensitive — JS may emit camelCase.
        new object[] { """{"audioOutputs":[]}""",                                         true  },
        new object[] { """{"preserveOriginalAudio":false}""",                             true  },

        // Both shapes coexisting (mid-upgrade) — new keys present → respect them.
        new object[] { """{"AudioCodec":"aac","TwoChannelAudio":true,"AudioOutputs":[]}""", true },
    };

    [Theory]
    [MemberData(nameof(ShapeDetectionRows))]
    public void HasNewAudioShape_detects_new_shape_keys(string rawJson, bool expected)
    {
        SettingsController.HasNewAudioShape(rawJson).Should().Be(expected);
    }


    [Fact]
    public void HasNewAudioShape_returns_false_for_invalid_json()
    {
        SettingsController.HasNewAudioShape("not json").Should().BeFalse();
        SettingsController.HasNewAudioShape("[]").Should().BeFalse();   // top-level array, not object
        SettingsController.HasNewAudioShape("").Should().BeFalse();
    }


    // =====================================================================
    //  MigrateLegacyAudioIfNeeded — the gated wrapper.
    // =====================================================================

    [Fact]
    public void Migration_runs_when_raw_json_lacks_both_new_keys()
    {
        const string legacy = """{"AudioCodec":"aac","TwoChannelAudio":true,"AudioBitrateKbps":192}""";
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(legacy, Opts)!;

        SettingsController.MigrateLegacyAudioIfNeeded(parsed, legacy);

        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().ContainSingle();
        parsed.AudioOutputs[0].Codec.Should().Be("aac");
        parsed.AudioOutputs[0].Layout.Should().Be("Stereo");
        parsed.AudioOutputs[0].BitrateKbps.Should().Be(192);
    }


    [Fact]
    public void Migration_is_skipped_when_raw_json_has_AudioOutputs_key()
    {
        // The user's bug, exact: empty AudioOutputs in the saved file. Migration must
        // NOT see this as "legacy" and rebuild rows from AudioCodec defaults.
        const string newShape = """
            {
                "PreserveOriginalAudio": false,
                "AudioOutputs": []
            }
            """;
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(newShape, Opts)!;
        // Pre-state: deserialize sets the legacy default AudioCodec="copy".
        parsed.AudioCodec.Should().Be("copy");

        SettingsController.MigrateLegacyAudioIfNeeded(parsed, newShape);

        // User's explicit choices preserved — neither the empty list nor the false flag
        // got clobbered by the migration's "copy" branch.
        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Migration_is_skipped_when_raw_json_has_PreserveOriginalAudio_key_only()
    {
        const string newShape = """{"PreserveOriginalAudio":false}""";
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(newShape, Opts)!;

        SettingsController.MigrateLegacyAudioIfNeeded(parsed, newShape);

        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Migration_is_skipped_even_when_legacy_fields_are_also_present()
    {
        // Mid-upgrade scenario: an old file got opened in the new form and re-saved.
        // The new form doesn't strip the legacy keys (it just doesn't include them in
        // its output), but the on-disk file may still have them from before. As long
        // as ANY new-shape key is present, the user's explicit empty list wins.
        const string mixed = """
            {
                "AudioCodec":            "aac",
                "AudioBitrateKbps":      192,
                "TwoChannelAudio":       true,
                "PreserveOriginalAudio": false,
                "AudioOutputs":          []
            }
            """;
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(mixed, Opts)!;

        SettingsController.MigrateLegacyAudioIfNeeded(parsed, mixed);

        parsed.PreserveOriginalAudio.Should().BeFalse();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Migration_runs_for_pure_default_options_without_audio_keys()
    {
        // A file written by some path that omits all audio keys entirely. The model
        // defaults to AudioCodec="copy", so migration's copy-branch flips
        // PreserveOriginalAudio to true and leaves AudioOutputs empty — the historical
        // "remux only" default behavior.
        const string minimal = """{"Format":"mkv","TargetBitrate":3500}""";
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(minimal, Opts)!;
        // Default-constructed PreserveOriginalAudio is true, so this test would pass
        // either way — but the assertion locks the behavior in place.
        parsed.PreserveOriginalAudio = false;   // simulate a stale value somehow set

        SettingsController.MigrateLegacyAudioIfNeeded(parsed, minimal);

        parsed.PreserveOriginalAudio.Should().BeTrue();
        parsed.AudioOutputs.Should().BeEmpty();
    }


    // =====================================================================
    //  Round-trip regression: simulates the exact user bug and proves the
    //  fix prevents AAC from re-appearing across save/reload cycles.
    // =====================================================================

    [Fact]
    public void User_bug_repro_deleting_all_rows_does_not_re_inject_AAC_on_reload()
    {
        // Step 1: user accidentally adds an AAC stereo output. The form auto-saves.
        var step1 = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs = new()
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
            },
        };
        string diskAfterAdd = JsonSerializer.Serialize(step1, Opts);

        // Step 2: user deletes the row. With the row-removal fix in encoder-form.js,
        // the auto-save fires and the disk file is overwritten with empty AudioOutputs.
        var step2 = new EncoderOptions
        {
            PreserveOriginalAudio = false,   // stays false because the checkbox didn't change
            AudioOutputs          = new(),   // user-deleted
        };
        string diskAfterDelete = JsonSerializer.Serialize(step2, Opts);

        // Step 3: page reload. The controller reads the file and runs the gated
        // migration. Because the saved JSON has the new-shape keys, migration is a
        // no-op and the user's explicit choices survive.
        var reloaded = JsonSerializer.Deserialize<EncoderOptions>(diskAfterDelete, Opts)!;
        SettingsController.MigrateLegacyAudioIfNeeded(reloaded, diskAfterDelete);

        reloaded.PreserveOriginalAudio.Should().BeFalse();
        reloaded.AudioOutputs.Should().BeEmpty();
    }


    [Fact]
    public void Pre_update_settings_get_migrated_exactly_once_then_become_authoritative()
    {
        // Step 1: the file pre-dates the audio expansion entirely — only legacy keys.
        const string preUpdate = """{"Format":"mkv","AudioCodec":"aac","TwoChannelAudio":true,"AudioBitrateKbps":192}""";

        var firstLoad = JsonSerializer.Deserialize<EncoderOptions>(preUpdate, Opts)!;
        SettingsController.MigrateLegacyAudioIfNeeded(firstLoad, preUpdate);

        firstLoad.PreserveOriginalAudio.Should().BeFalse();
        firstLoad.AudioOutputs.Should().ContainSingle();

        // Step 2: the user makes any change. The form auto-saves, writing back the
        // full options object including the new-shape keys.
        string postSaveJson = JsonSerializer.Serialize(firstLoad, Opts);

        var secondLoad = JsonSerializer.Deserialize<EncoderOptions>(postSaveJson, Opts)!;
        SettingsController.MigrateLegacyAudioIfNeeded(secondLoad, postSaveJson);

        // Migration was a no-op the second time — secondLoad reflects firstLoad exactly.
        secondLoad.PreserveOriginalAudio.Should().Be(firstLoad.PreserveOriginalAudio);
        secondLoad.AudioOutputs.Should().BeEquivalentTo(firstLoad.AudioOutputs,
            cfg => cfg.WithoutStrictOrdering());
    }
}
