using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Atomic JSON load/save for top-level config files (notifications.json,
///     integrations.json, auth.json). Follows the same write-then-rename pattern
///     used by settings.json, including a .bak fallback on corruption.
/// </summary>
public sealed class ConfigFileService
{
    private readonly FileService _fileService;
    private readonly object      _lock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public ConfigFileService(FileService fileService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        _fileService = fileService;
    }

    /******************************************************************
     *  Public API
     ******************************************************************/

    /// <summary>
    ///     Returns the absolute path for the given config filename under the
    ///     application's config directory, creating the directory if it does not exist.
    /// </summary>
    /// <param name="filename"> The config file name (e.g. "notifications.json"). </param>
    public string GetConfigPath(string filename)
    {
        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, filename);
    }

    /// <summary>
    ///     Deserializes a config file, falling back to the <c>.bak</c> copy on corruption
    ///     and to a default instance if neither file exists or can be parsed.
    /// </summary>
    /// <typeparam name="T"> The config model type. Must have a parameterless constructor. </typeparam>
    /// <param name="filename"> The config file name to load. </param>
    public T Load<T>(string filename) where T : new()
    {
        var path   = GetConfigPath(filename);
        var backup = path + ".bak";

        foreach (var candidate in new[] { path, backup })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var json   = File.ReadAllText(candidate);
                var parsed = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                if (parsed != null) return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Config file corrupted ({candidate}): {ex.Message}");
            }
        }
        return new T();
    }

    /// <summary>
    ///     Serializes <paramref name="value"/> and writes it atomically via a temp file,
    ///     preserving the previous version as <c>.bak</c>.
    /// </summary>
    /// <typeparam name="T"> The config model type. </typeparam>
    /// <param name="filename"> The config file name to write. </param>
    /// <param name="value"> The config value to persist. </param>
    public void Save<T>(string filename, T value)
    {
        var path   = GetConfigPath(filename);
        var backup = path + ".bak";
        var temp   = path + ".tmp";
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            File.WriteAllText(temp, json);
            if (File.Exists(path))
                File.Copy(path, backup, overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
    }
}
