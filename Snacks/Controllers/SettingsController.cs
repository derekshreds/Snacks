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
    private readonly TranscodingService  _transcodingService;
    private readonly FileService         _fileService;
    private readonly MediaFileRepository _mediaFileRepo;
    private readonly ILogger<SettingsController>? _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented            = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsController(
        TranscodingService transcodingService,
        FileService fileService,
        MediaFileRepository mediaFileRepo,
        ILogger<SettingsController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        _transcodingService = transcodingService;
        _fileService        = fileService;
        _mediaFileRepo      = mediaFileRepo;
        _log                = logger;
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
                return SettingsResponse(parsed);
            }
            catch
            {
                Console.WriteLine($"Settings file corrupted: {candidate}");
            }
        }

        // Fresh install / no saved file — defaults are already in the new shape, no migration.
        return SettingsResponse(new EncoderOptions());
    }

    /// <summary>
    ///     Applies env-var overrides and returns the options with an <c>_envLocked</c>
    ///     metadata array (camelCase dotted paths) so the settings form can render
    ///     env-driven fields read-only. Self-serialized camelCase — MVC's naming
    ///     policy does not rename <see cref="System.Text.Json.Nodes.JsonNode"/> keys.
    /// </summary>
    private static IActionResult SettingsResponse(EncoderOptions options)
    {
        EnvConfigOverrides.Apply(options, EnvConfigOverrides.SettingsPrefix);
        var node = JsonSerializer.SerializeToNode(options, _responseJsonOptions)!.AsObject();
        node["_envLocked"] = new System.Text.Json.Nodes.JsonArray(
            EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions))
                .Select(p => (System.Text.Json.Nodes.JsonNode)p).ToArray());
        return new JsonResult(node);
    }

    private static readonly JsonSerializerOptions _responseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

            // Merge the incoming form payload OVER the existing file instead of
            // replacing it wholesale. The settings form builds its payload from
            // scratch and only knows about fields it renders — properties with
            // no UI (e.g. Music.VbrQuality, hand-edited extras) used to be
            // silently deleted on the first auto-save.
            var json = MergeWithExistingSettings(path, settings);

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
                    // Env overrides beat whatever was just persisted — the in-memory
                    // options must always reflect the env-effective configuration.
                    EnvConfigOverrides.Apply(parsed, EnvConfigOverrides.SettingsPrefix);
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

    /// <summary>
    ///     Returns the JSON to persist: the incoming payload deep-merged over the
    ///     current settings.json (incoming values win; keys absent from the incoming
    ///     payload are preserved). Falls back to the incoming payload verbatim when
    ///     the existing file is missing or unparsable.
    /// </summary>
    internal static string MergeWithExistingSettings(string path, JsonElement settings)
    {
        var incomingJson = JsonSerializer.Serialize(settings, _jsonOptions);
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(incomingJson) is not System.Text.Json.Nodes.JsonObject incoming)
                return incomingJson;

            // Keys currently driven by SNACKS_SET_* env vars never reach the file —
            // the file keeps the last non-env value so unsetting the var reverts cleanly.
            EnvConfigOverrides.StripLockedPaths(incoming, EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions));
            incomingJson = incoming.ToJsonString(_jsonOptions);

            if (!System.IO.File.Exists(path))
                return incomingJson;
            if (System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject existing)
                return incomingJson;

            DeepMergeJson(existing, incoming);
            return existing.ToJsonString(_jsonOptions);
        }
        catch
        {
            return incomingJson;
        }
    }

    /// <summary> Recursively copies <paramref name="source"/> properties into <paramref name="target"/>; nested objects merge, everything else (incl. arrays) replaces. </summary>
    private static void DeepMergeJson(System.Text.Json.Nodes.JsonObject target, System.Text.Json.Nodes.JsonObject source)
    {
        foreach (var prop in source.ToList())
        {
            if (prop.Value is System.Text.Json.Nodes.JsonObject srcObj
                && target[prop.Key] is System.Text.Json.Nodes.JsonObject tgtObj)
            {
                DeepMergeJson(tgtObj, srcObj);
            }
            else
            {
                target[prop.Key] = prop.Value?.DeepClone();
            }
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
                return EnvConfigOverrides.Apply(parsed, EnvConfigOverrides.SettingsPrefix);
            }
            catch { /* fall through to backup or default */ }
        }
        return null;
    }

    /******************************************************************
     *  Presets
     ******************************************************************/

    /// <summary> Serialized lock for preset-file read-modify-write cycles. </summary>
    private static readonly object _presetsLock = new();

    /// <summary> Hard cap on stored presets — they're tiny, this is an anti-runaway guard. </summary>
    private const int MaxPresets = 50;

    /// <summary> One named, shareable snapshot of encoder options. </summary>
    public sealed class SavedPreset
    {
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        ///     Kept as a raw JSON element (not <see cref="EncoderOptions"/>) so presets
        ///     exported by a newer version round-trip through an older one untouched.
        /// </summary>
        public JsonElement Options { get; set; }
    }

    /// <summary> Body for save/import. <c>Name</c> + the options snapshot. </summary>
    public sealed class PresetRequest
    {
        public string Name { get; set; } = "";
        public JsonElement Options { get; set; }
    }

    /// <summary> Lists all saved presets (name, creation time, full options snapshot). </summary>
    [HttpGet("presets")]
    public IActionResult GetPresets()
    {
        // Same lock the writers hold: an unlocked ReadAllText racing the
        // writers' File.Move(overwrite: true) fails the move on Windows.
        lock (_presetsLock)
        {
            return new JsonResult(new { presets = LoadPresets() });
        }
    }

    /// <summary>
    ///     Saves (or overwrites, matched case-insensitively by name) a preset. The options
    ///     snapshot comes from the client's current form state, so a preset captures
    ///     everything the form knows — video, audio, subtitles, music.
    /// </summary>
    [HttpPost("presets")]
    public IActionResult SavePreset([FromBody] PresetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 80) return BadRequest("Preset name must be 1–80 characters");
        if (request.Options.ValueKind != JsonValueKind.Object) return BadRequest("Preset options must be an object");

        lock (_presetsLock)
        {
            var presets = LoadPresets();
            presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (presets.Count >= MaxPresets)
                return BadRequest($"Preset limit reached ({MaxPresets}) — delete one first");
            presets.Add(new SavedPreset { Name = name, Options = request.Options.Clone() });
            WritePresets(presets);
        }
        return new JsonResult(new { success = true });
    }

    /// <summary> Deletes a preset by name (case-insensitive). </summary>
    [HttpDelete("presets/{name}")]
    public IActionResult DeletePreset(string name)
    {
        lock (_presetsLock)
        {
            var presets = LoadPresets();
            int removed = presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound("No preset with that name");
            WritePresets(presets);
        }
        return new JsonResult(new { success = true });
    }

    /// <summary>
    ///     Downloads a preset as a standalone <c>.json</c> file for sharing. The
    ///     <c>snacksPreset</c> version marker lets the import path (and, later, the
    ///     community preset site) validate the file shape.
    /// </summary>
    [HttpGet("presets/export/{name}")]
    public IActionResult ExportPreset(string name)
    {
        SavedPreset? preset;
        lock (_presetsLock)
        {
            preset = LoadPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        if (preset == null) return NotFound("No preset with that name");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            snacksPreset = 1,
            name         = preset.Name,
            createdAt    = preset.CreatedAt,
            options      = preset.Options,
        }, _jsonOptions);

        var safeName = string.Concat(preset.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (safeName.Length == 0) safeName = "preset";
        return File(payload, "application/json", $"{safeName}.snacks-preset.json");
    }

    /// <summary>
    ///     Imports a preset file produced by <see cref="ExportPreset"/> (the client reads
    ///     the file and posts its parsed JSON). Same name-collision rule as save: an
    ///     existing preset with the same name is replaced.
    /// </summary>
    [HttpPost("presets/import")]
    public IActionResult ImportPreset([FromBody] JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object
            || !file.TryGetProperty("snacksPreset", out _)
            || !file.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
            || !file.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Object)
        {
            return BadRequest("Not a Snacks preset file");
        }

        return SavePreset(new PresetRequest { Name = nameEl.GetString() ?? "", Options = optionsEl });
    }

    /// <summary> Reads the preset list, tolerating a missing or corrupt file. </summary>
    private List<SavedPreset> LoadPresets()
    {
        var path = GetPresetsPath();
        if (!System.IO.File.Exists(path)) return new List<SavedPreset>();
        try
        {
            return JsonSerializer.Deserialize<List<SavedPreset>>(System.IO.File.ReadAllText(path), _jsonOptions)
                   ?? new List<SavedPreset>();
        }
        catch
        {
            Console.WriteLine($"Presets file corrupted: {path}");
            return new List<SavedPreset>();
        }
    }

    /// <summary> Atomic write-then-rename, mirroring the settings.json persistence pattern. </summary>
    private void WritePresets(List<SavedPreset> presets)
    {
        var path = GetPresetsPath();
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(presets, _jsonOptions));
        System.IO.File.Move(temp, path, overwrite: true);
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

    private string GetPresetsPath()
    {
        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "presets.json");
    }
}
