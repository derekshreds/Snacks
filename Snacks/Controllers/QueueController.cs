using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
using Snacks.Models;
using Snacks.Models.Requests;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Work-queue inspection, cancellation, retry, and pause endpoints.
///     Coordinates between the transcoding service, auto-scan service, and cluster service
///     so that a single pause toggle affects all active processing paths.
/// </summary>
[Route("api/queue")]
[ApiController]
public sealed class QueueController : ControllerBase
{
    private readonly TranscodingService    _transcodingService;
    private readonly MediaFileRepository   _mediaFileRepo;
    private readonly AutoScanService       _autoScanService;
    private readonly ClusterService        _clusterService;

    public QueueController(
        TranscodingService transcodingService,
        MediaFileRepository mediaFileRepo,
        AutoScanService autoScanService,
        ClusterService clusterService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        ArgumentNullException.ThrowIfNull(autoScanService);
        ArgumentNullException.ThrowIfNull(clusterService);
        _transcodingService = transcodingService;
        _mediaFileRepo      = mediaFileRepo;
        _autoScanService    = autoScanService;
        _clusterService     = clusterService;
    }

    /******************************************************************
     *  Queue Items
     ******************************************************************/

    /// <summary>
    ///     Returns paginated queue items. Active items (processing/uploading/downloading) are
    ///     always returned in full regardless of pagination and are excluded from the sorted
    ///     queue list. Remaining items are sorted by status then bitrate descending.
    /// </summary>
    /// <param name="limit"> Maximum number of non-active items to return. </param>
    /// <param name="skip"> Number of non-active items to skip before returning. </param>
    /// <param name="status"> Optional status filter applied before pagination. </param>
    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        [FromQuery] int? limit = null,
        [FromQuery] int skip = 0,
        [FromQuery] string? status = null)
    {
        var memoryItems = _transcodingService.GetAllWorkItems();

        var processingItems = memoryItems.Where(w => w.Status is WorkItemStatus.Processing
            or WorkItemStatus.Uploading or WorkItemStatus.Downloading).ToList();

        // Remote dispatch briefly leaves the DB row Queued while the in-memory
        // item is already Processing/Uploading — filter those rows out of the
        // pending list so the same file never shows a pending tile AND a
        // processing card at once.
        var activePaths = new HashSet<string>(
            processingItems.Select(w => w.NormalizedPath), StringComparer.OrdinalIgnoreCase);

        // Pending comes from the DB — the authoritative queue. The in-memory
        // registry only holds the hydrated window (whose rows are still Queued in
        // the DB), so memory Pending items are deliberately excluded here to avoid
        // double-listing. Everything terminal (recent history) stays memory-sourced.
        var terminalItems = memoryItems.Where(w => w.Status is not WorkItemStatus.Processing
            and not WorkItemStatus.Uploading and not WorkItemStatus.Downloading
            and not WorkItemStatus.Pending).ToList();

        terminalItems.Sort((a, b) =>
        {
            int StatusPriority(WorkItemStatus s) => s switch
            {
                WorkItemStatus.Completed => 0,
                WorkItemStatus.Failed    => 1,
                WorkItemStatus.Cancelled => 2,
                _                        => 3
            };
            int cmp = StatusPriority(a.Status).CompareTo(StatusPriority(b.Status));
            return cmp != 0 ? cmp : TranscodingService.CompareQueueOrder(a, b);
        });

        bool hasFilter = !string.IsNullOrEmpty(status)
            && Enum.TryParse<WorkItemStatus>(status, true, out var filterStatus);

        // Pending-only filter: page straight from the DB.
        if (hasFilter && string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            var (rows, pendingTotal) = await _mediaFileRepo.GetQueuedPageAsync(skip, limit ?? int.MaxValue, _transcodingService.QueueNewestFirst);
            return new JsonResult(new
            {
                items = rows.Where(r => !activePaths.Contains(r.FilePath)).Select(ToPendingDto).ToList(),
                total = pendingTotal,
                processing = processingItems,
            });
        }

        // Other status filters: memory-sourced recent history, as before.
        if (hasFilter)
        {
            Enum.TryParse<WorkItemStatus>(status, true, out var fs);
            var filtered = terminalItems.Where(w => w.Status == fs).ToList();
            var page = filtered.Skip(skip).Take(limit ?? int.MaxValue).Cast<object>().ToList();
            return new JsonResult(new { items = page, total = filtered.Count, processing = processingItems });
        }

        // No filter: the page spans [DB pending..., memory terminal...] in order.
        int dbPending = await _mediaFileRepo.CountQueuedLocalAsync();
        int combinedTotal = dbPending + terminalItems.Count;
        int take = limit ?? Math.Max(0, combinedTotal - skip);

        var items = new List<object>();
        if (skip < dbPending && take > 0)
        {
            var (rows, _) = await _mediaFileRepo.GetQueuedPageAsync(skip, Math.Min(take, dbPending - skip), _transcodingService.QueueNewestFirst);
            items.AddRange(rows.Where(r => !activePaths.Contains(r.FilePath)).Select(ToPendingDto));
        }
        int remaining = take - items.Count;
        if (remaining > 0)
        {
            int terminalSkip = Math.Max(0, skip - dbPending);
            items.AddRange(terminalItems.Skip(terminalSkip).Take(remaining));
        }

        return new JsonResult(new { items, total = combinedTotal, processing = processingItems });
    }

    /// <summary>
    ///     Projects a pending DB row into the shape the queue cards render. The id is
    ///     the row-addressed form (<c>mf-{rowId}</c>) — cancel/stop/prioritize accept
    ///     it directly, reaching the hydrated window copy when one exists.
    /// </summary>
    private static object ToPendingDto(MediaFile row) => new
    {
        id        = $"mf-{row.Id}",
        fileName  = row.FileName,
        path      = row.FilePath,
        size      = row.FileSize,
        bitrate   = row.Bitrate,
        length    = row.Duration,
        status    = "Pending",
        kind      = row.Kind.ToString(),
        priority  = row.Priority,
        createdAt = row.CreatedAt,
    };

    /// <summary> Returns aggregate counts of work items by status. </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var (pending, processing, completed, failed, total) = await _transcodingService.GetWorkItemCountsAsync();
        return new JsonResult(new { pending, processing, completed, failed, total });
    }

    /// <summary> Returns a single work item by ID, or 404 if not found. </summary>
    /// <param name="id"> The work item ID. </param>
    [HttpGet("item/{id}")]
    public IActionResult GetItem(string id)
    {
        var workItem = _transcodingService.GetWorkItem(id);
        return workItem == null ? NotFound() : new JsonResult(workItem);
    }

    /// <summary> Returns the persisted FFmpeg log lines for a work item. </summary>
    /// <param name="id"> The work item ID. </param>
    [HttpGet("logs/{id}")]
    public IActionResult GetLogs(string id) => new JsonResult(_transcodingService.GetWorkItemLogs(id));

    /******************************************************************
     *  Queue Control
     ******************************************************************/

    /// <summary> Cancels a queued or in-progress work item. </summary>
    /// <param name="id"> The work item ID to cancel. </param>
    [HttpPost("cancel/{id}")]
    public async Task<IActionResult> Cancel(string id)
    {
        try
        {
            await _transcodingService.CancelWorkItemAsync(id);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Moves a pending work item to the front of the queue. Accepts a hydrated
    ///     work-item GUID or the queue UI's <c>mf-{rowId}</c> form. 404 when the item
    ///     is unknown or no longer pending (it may have started processing between
    ///     render and click — the UI just refreshes in that case).
    /// </summary>
    /// <param name="id"> The work item ID to prioritize. </param>
    [HttpPost("prioritize/{id}")]
    public async Task<IActionResult> Prioritize(string id)
        => await _transcodingService.PrioritizeWorkItemAsync(id)
            ? new JsonResult(new { success = true })
            : NotFound("Item is not pending");

    /// <summary> Stops the active FFmpeg process for a work item without cancelling it. </summary>
    /// <param name="id"> The work item ID to stop. </param>
    [HttpPost("stop/{id}")]
    public async Task<IActionResult> Stop(string id)
    {
        try
        {
            await _transcodingService.StopWorkItemAsync(id);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Re-queues a previously failed file for encoding. Clears the failure state on the
    ///     persisted row, then immediately re-adds the file under the current encoder options
    ///     so the user sees it back in the queue without waiting for the next AutoScan.
    /// </summary>
    /// <param name="request"> Contains the file path to retry. </param>
    [HttpPost("retry")]
    public async Task<IActionResult> Retry([FromBody] RetryRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.FilePath)) return BadRequest("File path is required");
        await _transcodingService.RetryFileAsync(request.FilePath);
        try
        {
            await _autoScanService.AddSingleFileAsync(request.FilePath);
        }
        catch (Exception ex)
        {
            // Re-add failed (file no longer on disk, probe failed, exclusion rules now drop it,
            // etc.). DB row is already reset, so the next scan will see the file as Unseen and
            // try again — surface the message so the UI can toast it without rolling back.
            return BadRequest($"Retry queued but re-add failed: {ex.Message}");
        }
        return new JsonResult(new { success = true });
    }

    /// <summary> Returns all files recorded as failed in the persistent database. </summary>
    [HttpGet("failed")]
    public async Task<IActionResult> GetFailed()
    {
        var files = await _mediaFileRepo.GetFailedFilesAsync();
        return new JsonResult(files);
    }

    /// <summary>
    ///     Deletes every Failed row from the persistent database. The next library scan
    ///     re-discovers any source file still on disk; items whose source has already been
    ///     replaced (the bogus-failure backlog from the source-removed bug) simply stay gone,
    ///     since their original path no longer exists for AutoScan to find. No video files
    ///     are touched.
    /// </summary>
    [HttpDelete("failed")]
    public async Task<IActionResult> DeleteFailed()
    {
        var deleted = await _mediaFileRepo.DeleteAllFailedAsync();
        return new JsonResult(new { success = true, deleted });
    }

    /******************************************************************
     *  Pause State
     ******************************************************************/

    /// <summary>
    ///     Pauses or resumes all local encoding. When in node mode, also propagates the
    ///     pause state to the cluster node.
    /// </summary>
    /// <param name="request"> Contains the desired paused state. </param>
    [HttpPost("paused")]
    public IActionResult SetPaused([FromBody] PauseRequest request)
    {
        _transcodingService.SetPaused(request.Paused);
        _autoScanService.SetQueuePaused(request.Paused);
        if (_clusterService.IsNodeMode) _clusterService.SetNodePaused(request.Paused);
        return new JsonResult(new { success = true, paused = _transcodingService.IsPaused });
    }

    /// <summary> Returns the current queue pause state, accounting for both local and node pause flags. </summary>
    [HttpGet("paused")]
    public IActionResult GetPaused()
    {
        var paused = _transcodingService.IsPaused || _clusterService.IsNodePaused;
        return new JsonResult(new { paused });
    }
}
