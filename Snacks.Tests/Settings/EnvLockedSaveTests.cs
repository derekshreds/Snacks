using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Snacks.Controllers;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     The settings save path must never let SNACKS_SET_* values reach settings.json:
///     env-locked keys are stripped from the incoming payload before the deep-merge,
///     so the file keeps the last non-env value and unsetting the var reverts cleanly.
///     Shares the static env snapshot with the other override tests, hence the collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class EnvLockedSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("snacks-envlock-").FullName;

    public void Dispose()
    {
        EnvConfigOverrides.SetEnvironmentForTesting(null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void SetEnv(params (string Key, string Value)[] vars)
        => EnvConfigOverrides.SetEnvironmentForTesting(vars.ToDictionary(v => v.Key, v => v.Value));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Locked_keys_never_reach_the_merged_json()
    {
        SetEnv(("SNACKS_SET_Codec", "av1"), ("SNACKS_SET_Music__BitrateKbps", "256"));
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"Codec":"h265","TargetBitrate":3500,"Music":{"BitrateKbps":192}}""");

        var merged = (JsonObject)JsonNode.Parse(SettingsController.MergeWithExistingSettings(
            path, Parse("""{"Codec":"av1","TargetBitrate":9999,"Music":{"BitrateKbps":256}}""")))!;

        merged["Codec"]!.GetValue<string>().Should().Be("h265");            // file value kept
        merged["TargetBitrate"]!.GetValue<int>().Should().Be(9999);         // unlocked key merged
        merged["Music"]!["BitrateKbps"]!.GetValue<int>().Should().Be(192);  // nested file value kept
    }

    [Fact]
    public void Locked_keys_are_stripped_even_when_no_file_exists()
    {
        SetEnv(("SNACKS_SET_Codec", "av1"));
        var path = Path.Combine(_dir, "settings.json"); // never written

        var merged = (JsonObject)JsonNode.Parse(SettingsController.MergeWithExistingSettings(
            path, Parse("""{"Codec":"av1","TargetBitrate":1234}""")))!;

        merged.ContainsKey("Codec").Should().BeFalse();
        merged["TargetBitrate"]!.GetValue<int>().Should().Be(1234);
    }

    [Fact]
    public void Env_override_beats_the_legacy_audio_migration()
    {
        SetEnv(("SNACKS_SET_PreserveOriginalAudio", "false"));

        // Legacy-shaped file: migration would set PreserveOriginalAudio = true.
        var json   = """{"AudioCodec":"copy"}""";
        var parsed = JsonSerializer.Deserialize<EncoderOptions>(json)!;
        SettingsController.MigrateLegacyAudioIfNeeded(parsed, json);
        EnvConfigOverrides.Apply(parsed, EnvConfigOverrides.SettingsPrefix);

        parsed.PreserveOriginalAudio.Should().BeFalse();
    }
}
