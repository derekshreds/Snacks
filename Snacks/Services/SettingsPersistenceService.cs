using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Atomic load/save for <c>config/settings.json</c> shared by the settings
///     POST endpoint and the policy "apply" endpoint. Both paths must perform
///     the same atomic write-then-rename, the same presence-aware legacy audio
///     migration, and the same <see cref="TranscodingService.UpdateOptions"/>
///     push so the in-memory and on-disk views never disagree.
///
///     Kept distinct from <see cref="ConfigFileService"/> because the settings
///     write path is settings-specific: it accepts a raw <see cref="JsonElement"/>
///     to round-trip unknown fields, and it runs the legacy audio migration.
/// </summary>
public sealed class SettingsPersistenceService
{
    public const string SettingsFileName = "settings.json";

    private readonly FileService        _fileService;
    private readonly TranscodingService _transcodingService;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsPersistenceService(FileService fileService, TranscodingService transcodingService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(transcodingService);
        _fileService        = fileService;
        _transcodingService = transcodingService;
    }

    /// <summary>
    ///     Returns the absolute path to <c>config/settings.json</c>, creating the
    ///     config directory if it doesn't exist.
    /// </summary>
    public string GetSettingsPath()
    {
        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, SettingsFileName);
    }

    /// <summary>
    ///     Persists the given <see cref="JsonElement"/> to <c>settings.json</c> atomically
    ///     and pushes the resulting <see cref="EncoderOptions"/> into the in-memory
    ///     transcoding service. The pre-existing file is retained as <c>.bak</c>.
    /// </summary>
    public void PersistAndActivate(JsonElement settings)
    {
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        PersistAndActivate(json);
    }

    /// <summary>
    ///     Persists the given serialized JSON to <c>settings.json</c> atomically and
    ///     pushes the resulting <see cref="EncoderOptions"/> into the in-memory
    ///     transcoding service. Identical write semantics to the <see cref="JsonElement"/>
    ///     overload — both call this internal path so the disk format is consistent.
    /// </summary>
    public void PersistAndActivate(string json)
    {
        var path   = GetSettingsPath();
        var backup = path + ".bak";
        var temp   = path + ".tmp";

        File.WriteAllText(temp, json);
        if (File.Exists(path))
            File.Copy(path, backup, overwrite: true);
        File.Move(temp, path, overwrite: true);

        try
        {
            var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
            if (parsed != null)
            {
                MigrateLegacyAudioIfNeeded(parsed, json);
                _transcodingService.UpdateOptions(parsed);
            }
        }
        catch
        {
            /* non-fatal — in-memory options retain their previous values */
        }
    }

    /// <summary>
    ///     Persists the given <see cref="EncoderOptions"/> to disk and activates it
    ///     in the in-memory transcoding service. Convenience for callers (like the
    ///     policy "apply" endpoint) that already have a typed options instance.
    /// </summary>
    public void PersistAndActivate(EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var json = JsonSerializer.Serialize(options, _jsonOptions);
        PersistAndActivate(json);
    }

    /// <summary>
    ///     Loads the persisted options into a typed <see cref="EncoderOptions"/>,
    ///     applying the legacy audio migration only when the file pre-dates the new shape.
    ///     Falls back to <c>.bak</c> on corruption, then returns <see langword="null"/>
    ///     when neither file exists or parses.
    /// </summary>
    public EncoderOptions? Load()
    {
        var path   = GetSettingsPath();
        var backup = path + ".bak";

        foreach (var candidate in new[] { path, backup })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var json   = File.ReadAllText(candidate);
                var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (parsed == null) continue;
                MigrateLegacyAudioIfNeeded(parsed, json);
                return parsed;
            }
            catch { /* fall through to backup or default */ }
        }
        return null;
    }

    /// <summary>
    ///     Same load behavior as <see cref="Load"/> but also exposes the raw JSON text
    ///     of the file that was read. Used by the GET endpoint which returns the raw
    ///     shape (with migration applied in-memory) to the client.
    /// </summary>
    public (EncoderOptions? Options, string? RawJson) LoadWithRawJson()
    {
        var path   = GetSettingsPath();
        var backup = path + ".bak";

        foreach (var candidate in new[] { path, backup })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var json   = File.ReadAllText(candidate);
                var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (parsed == null) continue;
                MigrateLegacyAudioIfNeeded(parsed, json);
                return (parsed, json);
            }
            catch { /* fall through to backup or default */ }
        }
        return (null, null);
    }

    /// <summary>
    ///     Runs <see cref="EncoderOptions.ApplyLegacyAudioMigration"/> only when the
    ///     on-disk JSON predates the new audio shape — i.e., it carries no
    ///     <c>PreserveOriginalAudio</c> and no <c>AudioOutputs</c> keys.
    /// </summary>
    public static void MigrateLegacyAudioIfNeeded(EncoderOptions parsed, string rawJson)
    {
        if (HasNewAudioShape(rawJson)) return;
        parsed.ApplyLegacyAudioMigration();
    }

    /// <summary>
    ///     Returns <see langword="true"/> when the raw JSON carries either
    ///     <c>PreserveOriginalAudio</c> or <c>AudioOutputs</c> at the top level (case-insensitive).
    /// </summary>
    public static bool HasNewAudioShape(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "PreserveOriginalAudio", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(prop.Name, "AudioOutputs",          StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
