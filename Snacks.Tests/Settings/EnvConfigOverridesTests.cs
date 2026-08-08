using System.Text.Json.Nodes;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Core behavior of the SNACKS_SET_/SNACKS_SCAN_/SNACKS_INTEG_ environment override
///     layer: type conversion, nesting, case-insensitivity, the denylist, and the
///     strip/restore helpers that keep env values out of the config files.
///     All tests share the static env snapshot, so the class is serialized via a
///     dedicated xunit collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class EnvConfigOverridesTests : IDisposable
{
    public void Dispose() => EnvConfigOverrides.SetEnvironmentForTesting(null);

    private static void SetEnv(params (string Key, string Value)[] vars)
        => EnvConfigOverrides.SetEnvironmentForTesting(vars.ToDictionary(v => v.Key, v => v.Value));

    /******************************************************************
     *  Scalars, enums, nesting, case-insensitivity
     ******************************************************************/

    [Fact]
    public void Applies_string_int_and_bool_scalars()
    {
        SetEnv(("SNACKS_SET_Codec", "av1"),
               ("SNACKS_SET_TargetBitrate", "2500"),
               ("SNACKS_SET_TonemapHdrToSdr", "yes"),
               ("SNACKS_SET_RetryOnFail", "0"));

        var opts = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        opts.Codec.Should().Be("av1");
        opts.TargetBitrate.Should().Be(2500);
        opts.TonemapHdrToSdr.Should().BeTrue();
        opts.RetryOnFail.Should().BeFalse();
    }

    [Fact]
    public void Applies_enums_case_insensitively()
    {
        SetEnv(("SNACKS_SET_EncodingMode", "muxonly"),
               ("SNACKS_SET_MuxStreams", "AUDIO"));

        var opts = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        opts.EncodingMode.Should().Be(EncodingMode.MuxOnly);
        opts.MuxStreams.Should().Be(MuxStreams.Audio);
    }

    [Fact]
    public void Applies_nested_music_properties_via_double_underscore()
    {
        SetEnv(("SNACKS_SET_Music__BitrateKbps", "256"),
               ("SNACKS_SET_Music__Codec", "opus"));

        var opts = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        opts.Music.BitrateKbps.Should().Be(256);
        opts.Music.Codec.Should().Be("opus");
    }

    [Fact]
    public void Property_name_segments_match_case_insensitively()
    {
        SetEnv(("SNACKS_SET_TARGETBITRATE", "1234"),
               ("SNACKS_SET_music__bitratekbps", "320"));

        var opts = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        opts.TargetBitrate.Should().Be(1234);
        opts.Music.BitrateKbps.Should().Be(320);
    }

    [Fact]
    public void Hardware_acceleration_runs_through_the_normalizing_setter()
    {
        SetEnv(("SNACKS_SET_HardwareAcceleration", "nvenc"));

        EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix)
            .HardwareAcceleration.Should().Be("nvidia");
    }

    /******************************************************************
     *  Lists and complex JSON values
     ******************************************************************/

    [Fact]
    public void String_lists_accept_comma_separated_values()
    {
        SetEnv(("SNACKS_SET_AudioLanguagesToKeep", "en, ja ,de"));

        EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix)
            .AudioLanguagesToKeep.Should().Equal("en", "ja", "de");
    }

    [Fact]
    public void String_lists_accept_json_arrays()
    {
        SetEnv(("SNACKS_SET_SubtitleLanguagesToKeep", """["en","fr"]"""));

        EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix)
            .SubtitleLanguagesToKeep.Should().Equal("en", "fr");
    }

    [Fact]
    public void Complex_types_accept_json_object_arrays()
    {
        SetEnv(("SNACKS_SET_AudioOutputs", """[{"Codec":"aac","Layout":"Stereo","BitrateKbps":192}]"""));

        var outputs = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix).AudioOutputs;

        outputs.Should().HaveCount(1);
        outputs[0].Codec.Should().Be("aac");
        outputs[0].BitrateKbps.Should().Be(192);
    }

    [Fact]
    public void Scan_directories_honor_the_watched_folder_converter_for_plain_strings()
    {
        SetEnv(("SNACKS_SCAN_Directories", """["/media/tv","/media/movies"]"""));

        var config = EnvConfigOverrides.Apply(new AutoScanConfig(), EnvConfigOverrides.AutoScanPrefix);

        config.Directories.Select(d => d.Path).Should().Equal("/media/tv", "/media/movies");
    }

    [Fact]
    public void Applied_list_instances_are_not_shared_between_targets()
    {
        SetEnv(("SNACKS_SET_AudioLanguagesToKeep", "en"));

        var first  = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);
        var second = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);
        first.AudioLanguagesToKeep.Add("ja");

        second.AudioLanguagesToKeep.Should().Equal("en");
    }

    /******************************************************************
     *  Invalid input and the denylist
     ******************************************************************/

    [Fact]
    public void Unparsable_values_are_skipped_without_throwing()
    {
        SetEnv(("SNACKS_SET_TargetBitrate", "fast"));

        EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix)
            .TargetBitrate.Should().Be(3500); // default retained
    }

    [Fact]
    public void Unknown_property_names_are_skipped_without_throwing()
    {
        SetEnv(("SNACKS_SET_NoSuchSetting", "value"));

        var act = () => EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        act.Should().NotThrow();
    }

    [Fact]
    public void Denylisted_properties_are_never_applied_or_locked()
    {
        SetEnv(("SNACKS_SCAN_QueuePaused", "true"),
               ("SNACKS_SET_HardwareDevicePath", "/dev/dri/renderD128"));

        var scan = EnvConfigOverrides.Apply(new AutoScanConfig(), EnvConfigOverrides.AutoScanPrefix);
        var opts = EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);

        scan.QueuePaused.Should().BeFalse();
        opts.HardwareDevicePath.Should().BeNull();
        EnvConfigOverrides.LockedPaths(EnvConfigOverrides.AutoScanPrefix, typeof(AutoScanConfig)).Should().BeEmpty();
        EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions)).Should().BeEmpty();
    }

    /******************************************************************
     *  LockedPaths / StripLockedPaths / RestoreLockedValues
     ******************************************************************/

    [Fact]
    public void Locked_paths_are_camel_case_and_dotted()
    {
        SetEnv(("SNACKS_SET_Codec", "av1"),
               ("SNACKS_SET_Music__BitrateKbps", "256"),
               ("SNACKS_SET_TargetBitrate", "oops")); // unparsable — must not be locked

        var locked = EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions));

        locked.Should().BeEquivalentTo("codec", "music.bitrateKbps");
    }

    [Fact]
    public void Strip_removes_locked_keys_case_insensitively_including_nested()
    {
        SetEnv(("SNACKS_SET_Codec", "av1"),
               ("SNACKS_SET_Music__BitrateKbps", "256"));

        // The settings form posts PascalCase keys.
        var incoming = (JsonObject)JsonNode.Parse(
            """{"Codec":"h264","TargetBitrate":9999,"Music":{"BitrateKbps":128,"Format":"m4a"}}""")!;

        EnvConfigOverrides.StripLockedPaths(incoming, EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions));

        incoming.ContainsKey("Codec").Should().BeFalse();
        incoming["TargetBitrate"]!.GetValue<int>().Should().Be(9999);
        var music = (JsonObject)incoming["Music"]!;
        music.ContainsKey("BitrateKbps").Should().BeFalse();
        music["Format"]!.GetValue<string>().Should().Be("m4a");
    }

    [Fact]
    public void Restore_copies_locked_values_back_from_file_state()
    {
        SetEnv(("SNACKS_INTEG_Plex__Token", "env-token"));

        var incoming  = new IntegrationConfig { Plex = { Token = "env-token", BaseUrl = "http://new" } };
        var fileState = new IntegrationConfig { Plex = { Token = "file-token" } };

        EnvConfigOverrides.RestoreLockedValues(incoming, fileState, EnvConfigOverrides.IntegrationsPrefix);

        incoming.Plex.Token.Should().Be("file-token"); // env value kept out of the file
        incoming.Plex.BaseUrl.Should().Be("http://new"); // unlocked fields untouched
    }

    [Fact]
    public void Integration_sections_lock_via_section_and_property()
    {
        SetEnv(("SNACKS_INTEG_Sonarr__ApiKey", "xyz"),
               ("SNACKS_INTEG_Radarr__Enabled", "true"));

        var config = EnvConfigOverrides.Apply(new IntegrationConfig(), EnvConfigOverrides.IntegrationsPrefix);

        config.Sonarr.ApiKey.Should().Be("xyz");
        config.Radarr.Enabled.Should().BeTrue();
        EnvConfigOverrides.LockedPaths(EnvConfigOverrides.IntegrationsPrefix, typeof(IntegrationConfig))
            .Should().BeEquivalentTo("sonarr.apiKey", "radarr.enabled");
    }
}
