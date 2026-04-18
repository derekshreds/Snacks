using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Backward-compatibility migration for settings.json. Upgrades legacy
///     single-language flags (EnglishOnlyAudio / EnglishOnlySubtitles) to the
///     new multi-language keep-lists, and populates defaults for fields
///     introduced after the original settings file was written.
/// </summary>
public static class SettingsMigration
{
    /******************************************************************
     *  Public API
     ******************************************************************/

    /// <summary>
    ///     Applies all known migrations to a parsed <see cref="EncoderOptions"/>.
    ///     Returns <see langword="true"/> when any change was made and the file should be re-persisted.
    /// </summary>
    /// <param name="options"> The parsed encoder options to migrate in place. </param>
    public static bool Apply(EncoderOptions options)
    {
        var changed = false;

        if (options.AudioLanguagesToKeep == null || options.AudioLanguagesToKeep.Count == 0)
        {
            options.AudioLanguagesToKeep = options.EnglishOnlyAudio
                ? new List<string> { "en" }
                : new List<string> { "en" };
            changed = true;
        }

        if (options.SubtitleLanguagesToKeep == null || options.SubtitleLanguagesToKeep.Count == 0)
        {
            options.SubtitleLanguagesToKeep = options.EnglishOnlySubtitles
                ? new List<string> { "en" }
                : new List<string> { "en" };
            changed = true;
        }

        if (string.IsNullOrEmpty(options.AudioCodec))               { options.AudioCodec               = "aac";    changed = true; }
        if (options.AudioBitrateKbps <= 0)                          { options.AudioBitrateKbps         = 192;      changed = true; }
        if (string.IsNullOrEmpty(options.DownscalePolicy))          { options.DownscalePolicy          = "Never";  changed = true; }
        if (string.IsNullOrEmpty(options.DownscaleTarget))          { options.DownscaleTarget          = "1080p";  changed = true; }
        if (string.IsNullOrEmpty(options.FfmpegQualityPreset))      { options.FfmpegQualityPreset      = "medium"; changed = true; }
        if (string.IsNullOrEmpty(options.SidecarSubtitleFormat))    { options.SidecarSubtitleFormat    = "srt";    changed = true; }
        if (string.IsNullOrEmpty(options.OriginalLanguageProvider)) { options.OriginalLanguageProvider = "None";   changed = true; }

        return changed;
    }

    /// <summary>
    ///     Reads, migrates, and re-persists settings.json at startup. Idempotent;
    ///     a no-op on already-upgraded files.
    /// </summary>
    /// <param name="settingsPath"> Absolute path to the settings.json file. </param>
    public static void RunStartupMigration(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return;
        try
        {
            var json    = File.ReadAllText(settingsPath);
            var options = JsonSerializer.Deserialize<EncoderOptions>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (options == null) return;

            if (Apply(options))
            {
                var upgraded = JsonSerializer.Serialize(options, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
                var tmp = settingsPath + ".tmp";
                var bak = settingsPath + ".bak";
                File.WriteAllText(tmp, upgraded);
                File.Copy(settingsPath, bak, overwrite: true);
                File.Move(tmp, settingsPath, overwrite: true);
                Console.WriteLine("Migrated settings.json to new schema.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Settings migration skipped: {ex.Message}");
        }
    }
}
