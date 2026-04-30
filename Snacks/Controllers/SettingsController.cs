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

                MigrateLegacyAudioIfNeeded(parsed, json);
                return new JsonResult(parsed);
            }
            catch
            {
                Console.WriteLine($"Settings file corrupted: {candidate}");
            }
        }

        // Fresh install / no saved file — defaults are already in the new shape, no migration.
        return new JsonResult(new EncoderOptions());
    }

    /// <summary>
    ///     Runs <see cref="EncoderOptions.ApplyLegacyAudioMigration"/> only when the on-disk
    ///     JSON predates the new audio shape — i.e., it carries no <c>PreserveOriginalAudio</c>
    ///     and no <c>AudioOutputs</c> keys. Once the user's saved file contains the new shape
    ///     (even an empty <c>AudioOutputs</c> array), their settings are authoritative and we
    ///     never re-apply migration over their explicit choices. This is what fixes the
    ///     "delete all rows → AAC keeps coming back" loop: after the user saves with the new
    ///     shape, migration is permanently a no-op for that file.
    /// </summary>
    internal static void MigrateLegacyAudioIfNeeded(EncoderOptions parsed, string rawJson)
    {
        if (HasNewAudioShape(rawJson)) return;
        parsed.ApplyLegacyAudioMigration();
    }

    /// <summary>
    ///     Returns <see langword="true"/> when the raw JSON carries either
    ///     <c>PreserveOriginalAudio</c> or <c>AudioOutputs</c> at the top level (case-insensitive).
    ///     Either key's presence means the file was written by the new audio form, even if the
    ///     value is the empty default — and migration must respect that.
    /// </summary>
    internal static bool HasNewAudioShape(string rawJson)
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

    /// <summary>
    ///     Persists updated encoder settings using an atomic write-then-rename. The previous
    ///     file is retained as <c>.bak</c>. Updates the in-memory options on the transcoding
    ///     service so subsequent encodes pick up the change. Library re-evaluation (flipping
    ///     skipped/unseen rows under the new options, dropping now-obsolete pending items) is
    ///     intentionally NOT run here — it's a heavyweight DB + queue walk that doesn't belong
    ///     on every keystroke of an auto-saving form. Users trigger it manually via the
    ///     Re-evaluate Queue button on the Advanced settings panel.
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

            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /******************************************************************
     *  Manual queue re-evaluation
     ******************************************************************/

    /// <summary>
    ///     Concurrency guard for <see cref="Reevaluate"/>. <see cref="SemaphoreSlim"/> with
    ///     capacity 1 — a second request while one is in-flight is rejected with HTTP 409.
    ///     Static so the lock spans every controller instance for this process; each request
    ///     gets a fresh controller via DI but the gate is process-wide.
    /// </summary>
    private static readonly SemaphoreSlim _reevaluateLock = new(1, 1);

    /// <summary>
    ///     Walks the persisted media-file rows and the in-memory work queue, flipping skipped
    ///     and unseen rows according to the current encoder options and dropping any pending
    ///     items that no longer need encoding. This is the inverse-direction-aware version of
    ///     what used to run on every settings save.
    ///
    ///     Concurrent requests are rejected with HTTP 409 rather than queued or coalesced —
    ///     the operation is idempotent at the per-row level, but two simultaneous walks would
    ///     race on the queue mutation and waste DB work. The user-facing behavior is "the
    ///     button disables itself, you can click it again when it finishes."
    /// </summary>
    [HttpPost("reevaluate")]
    public async Task<IActionResult> Reevaluate()
    {
        if (!await _reevaluateLock.WaitAsync(0))
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                error   = "A re-evaluation is already in progress. Please wait for it to finish.",
            });
        }

        try
        {
            EncoderOptions? options = LoadOptions();
            if (options == null)
            {
                return new JsonResult(new
                {
                    success  = false,
                    error    = "No saved settings found.",
                });
            }

            // Pre-pass: resolve and persist OriginalLanguage for any rows that don't have one
            // yet, so WouldSkipUnderOptions sees the merged keep lists. Without this, the
            // ladder would mis-predict drops on files queued before the OriginalLanguage
            // cache existed (or before KeepOriginalLanguage was turned on). Cheap when the
            // integration provider caches per-show / per-movie.
            var skippedRows = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Skipped);
            var unseenRows  = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Unseen);
            var queuedRows  = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Queued);
            var allRows     = skippedRows.Concat(unseenRows).Concat(queuedRows).ToList();
            await _transcodingService.BackfillOriginalLanguageAsync(allRows, options);

            // Direction A: files newly eligible under current settings (Skipped → Unseen).
            // Legacy rows without stream summaries are kicked back to Unseen too so the
            // next scan re-probes them.
            int requeued = await _mediaFileRepo.ReevaluateSkippedAsync(mf =>
                mf.AudioStreams != null || mf.SubtitleStreams != null
                    ? TranscodingService.WouldSkipUnderOptions(mf, options)
                    : false);

            // Direction B: files no longer eligible (Unseen → Skipped).
            int reskipped = await _mediaFileRepo.ReevaluateUnseenAsync(mf =>
                TranscodingService.WouldSkipUnderOptions(mf, options));

            // Drop pending queue items that current settings would skip.
            int dequeued = await _transcodingService.RemoveSettingsObsoletedQueueItemsAsync(options);

            return new JsonResult(new { success = true, requeued, reskipped, dequeued });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
        finally
        {
            _reevaluateLock.Release();
        }
    }

    /// <summary>
    ///     Reads the persisted settings from disk into an <see cref="EncoderOptions"/>,
    ///     applying the legacy audio migration only when the file pre-dates the new shape.
    ///     Mirrors the read path in <see cref="Get"/> — kept as a private helper so the
    ///     re-evaluate endpoint and the GET endpoint always see the same shape.
    /// </summary>
    private EncoderOptions? LoadOptions()
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
                MigrateLegacyAudioIfNeeded(parsed, json);
                return parsed;
            }
            catch { /* fall through to backup or default */ }
        }
        return null;
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
