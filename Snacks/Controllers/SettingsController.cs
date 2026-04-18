using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Encoder settings persistence. Reads and writes <see cref="EncoderOptions"/>
///     at <c>config/settings.json</c> using an atomic write-then-rename pattern with
///     a <c>.bak</c> fallback on corruption.
/// </summary>
[Route("api/settings")]
[ApiController]
public sealed class SettingsController : ControllerBase
{
    private readonly TranscodingService _transcodingService;
    private readonly FileService        _fileService;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented            = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsController(TranscodingService transcodingService, FileService fileService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(fileService);
        _transcodingService = transcodingService;
        _fileService        = fileService;
    }

    /******************************************************************
     *  Settings Persistence
     ******************************************************************/

    /// <summary>
    ///     Loads the current encoder settings from disk, applying schema migrations if needed.
    ///     Falls back to the <c>.bak</c> file on corruption, then to defaults.
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        var path   = GetSettingsPath();
        var backup = path + ".bak";

        foreach (var candidate in new[] { path, backup })
        {
            if (!System.IO.File.Exists(candidate)) continue;
            try
            {
                var json    = System.IO.File.ReadAllText(candidate);
                var options = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (options != null && SettingsMigration.Apply(options))
                {
                    var upgraded = JsonSerializer.Serialize(options, _jsonOptions);
                    try
                    {
                        System.IO.File.Copy(candidate, candidate + ".bak", overwrite: true);
                        System.IO.File.WriteAllText(candidate, upgraded);
                    }
                    catch
                    {
                        /* best-effort — if the .bak write fails the upgraded content is still returned */
                    }
                    return Content(upgraded, "application/json");
                }
                return Content(json, "application/json");
            }
            catch
            {
                Console.WriteLine($"Settings file corrupted: {candidate}");
            }
        }
        return new JsonResult(new EncoderOptions());
    }

    /// <summary>
    ///     Persists updated encoder settings using an atomic write-then-rename. The previous
    ///     file is retained as <c>.bak</c>. Also updates the in-memory options on the
    ///     transcoding service.
    /// </summary>
    /// <param name="settings"> The raw JSON settings document to persist. </param>
    [HttpPost]
    public IActionResult Save([FromBody] JsonElement settings)
    {
        try
        {
            var path   = GetSettingsPath();
            var backup = path + ".bak";
            var temp   = path + ".tmp";
            var json   = JsonSerializer.Serialize(settings, _jsonOptions);

            System.IO.File.WriteAllText(temp, json);
            if (System.IO.File.Exists(path))
                System.IO.File.Copy(path, backup, overwrite: true);
            System.IO.File.Move(temp, path, overwrite: true);

            try
            {
                var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (parsed != null) _transcodingService.UpdateOptions(parsed);
            }
            catch
            {
                /* non-fatal — in-memory options retain their previous values */
            }

            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    private string GetSettingsPath()
    {
        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "settings.json");
    }
}
