using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private readonly TranscodingService          _transcodingService;
    private readonly MediaFileRepository         _mediaFileRepo;
    private readonly SettingsPersistenceService  _settingsPersistence;
    private readonly ILogger<SettingsController>? _log;

    public SettingsController(
        TranscodingService transcodingService,
        MediaFileRepository mediaFileRepo,
        SettingsPersistenceService settingsPersistence,
        ILogger<SettingsController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        ArgumentNullException.ThrowIfNull(settingsPersistence);
        _transcodingService  = transcodingService;
        _mediaFileRepo       = mediaFileRepo;
        _settingsPersistence = settingsPersistence;
        _log                 = logger;
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
        var (options, _) = _settingsPersistence.LoadWithRawJson();
        return new JsonResult(options ?? new EncoderOptions());
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
        => SettingsPersistenceService.MigrateLegacyAudioIfNeeded(parsed, rawJson);

    /// <summary>
    ///     Returns <see langword="true"/> when the raw JSON carries either
    ///     <c>PreserveOriginalAudio</c> or <c>AudioOutputs</c> at the top level (case-insensitive).
    ///     Either key's presence means the file was written by the new audio form, even if the
    ///     value is the empty default — and migration must respect that.
    /// </summary>
    internal static bool HasNewAudioShape(string rawJson)
        => SettingsPersistenceService.HasNewAudioShape(rawJson);

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
            _settingsPersistence.PersistAndActivate(settings);
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
    public async Task<IActionResult> Reevaluate([FromQuery] bool forceRetryNoSavings = false)
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
            EncoderOptions? options = _settingsPersistence.Load();
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

            // Opt-in retry of empirical no-savings outcomes. Default off because "we tried,
            // it didn't shrink" should not auto-retry on every Re-evaluate — that's the loop
            // the new NoSavings status was introduced to break. The user clicks this when
            // they've changed encoder settings (target bitrate, codec, etc.) and want files
            // that previously gave no savings re-evaluated under the new options.
            int retriedNoSavings = forceRetryNoSavings
                ? await _mediaFileRepo.ReevaluateNoSavingsAsync()
                : 0;

            // Surfaces the magnitude of each Re-evaluate run in the ops log so a future
            // "Re-evaluate flagged 1650 items as needing rescan" report has historical context.
            _log?.LogInformation(
                "ReevaluateRun requeuedSkipped={Requeued} reskippedUnseen={Reskipped} dequeuedPending={Dequeued} retriedNoSavings={RetriedNoSavings} forceRetryNoSavings={ForceRetry}",
                requeued, reskipped, dequeued, retriedNoSavings, forceRetryNoSavings);

            return new JsonResult(new { success = true, requeued, reskipped, dequeued, retriedNoSavings });
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
}
