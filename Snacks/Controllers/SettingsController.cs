using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
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
    private readonly TranscodingService  _transcodingService;
    private readonly FileService         _fileService;
    private readonly MediaFileRepository _mediaFileRepo;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented            = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsController(TranscodingService transcodingService, FileService fileService, MediaFileRepository mediaFileRepo)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        _transcodingService = transcodingService;
        _fileService        = fileService;
        _mediaFileRepo      = mediaFileRepo;
    }

    /******************************************************************
     *  Settings Persistence
     ******************************************************************/

    /// <summary>
    ///     Loads the current encoder settings from disk. Falls back to the
    ///     <c>.bak</c> file on corruption, then to defaults.
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
                var json   = System.IO.File.ReadAllText(candidate);
                var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (parsed == null) continue;

                // Normalize legacy audio fields into the new PreserveOriginalAudio + AudioOutputs
                // shape so the UI doesn't have to handle two shapes when restoring the form.
                parsed.ApplyLegacyAudioMigration();
                return new JsonResult(parsed);
            }
            catch
            {
                Console.WriteLine($"Settings file corrupted: {candidate}");
            }
        }

        var defaults = new EncoderOptions();
        defaults.ApplyLegacyAudioMigration();
        return new JsonResult(defaults);
    }

    /// <summary>
    ///     Persists updated encoder settings using an atomic write-then-rename. The previous
    ///     file is retained as <c>.bak</c>. Also updates the in-memory options on the
    ///     transcoding service and re-evaluates previously-skipped files — any whose skip
    ///     decision would no longer hold under the new options are flipped to Unseen so
    ///     the next scan picks them up.
    /// </summary>
    /// <param name="settings"> The raw JSON settings document to persist. </param>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] JsonElement settings)
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

            EncoderOptions? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions);
                if (parsed != null)
                {
                    parsed.ApplyLegacyAudioMigration();
                    _transcodingService.UpdateOptions(parsed);
                }
            }
            catch
            {
                /* non-fatal — in-memory options retain their previous values */
            }

            int requeued  = 0;
            int reskipped = 0;
            int dequeued  = 0;
            if (parsed != null)
            {
                // Direction A: settings change made files newly eligible for encoding.
                // Flip Skipped → Unseen so the next scan picks them up.
                // Legacy rows scanned before the stream-summary columns existed have null
                // AudioStreams/SubtitleStreams — we can't fully re-evaluate them, so flip
                // them to Unseen to force a re-probe on the next scan.
                requeued = await _mediaFileRepo.ReevaluateSkippedAsync(mf =>
                    mf.AudioStreams != null || mf.SubtitleStreams != null
                        ? TranscodingService.WouldSkipUnderOptions(mf, parsed)
                        : false);

                // Direction B: settings change made previously-eligible files no longer
                // need encoding (e.g., user added an audio output that re-queued a batch,
                // then removed it). Flip Unseen → Skipped, and drop matching Pending items
                // from the in-memory queue so they don't get encoded under settings the
                // user just reverted. Active jobs are left alone — too late to cancel
                // safely; the user can stop them via the queue UI if needed.
                reskipped = await _mediaFileRepo.ReevaluateUnseenAsync(mf =>
                    TranscodingService.WouldSkipUnderOptions(mf, parsed));
                dequeued = await _transcodingService.RemoveSettingsObsoletedQueueItemsAsync(parsed);
            }

            return new JsonResult(new { success = true, requeued, reskipped, dequeued });
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
