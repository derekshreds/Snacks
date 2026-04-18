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
    public IActionResult GetItems(
        [FromQuery] int? limit = null,
        [FromQuery] int skip = 0,
        [FromQuery] string? status = null)
    {
        var allItems = _transcodingService.GetAllWorkItems();

        var processingItems = allItems.Where(w => w.Status is WorkItemStatus.Processing
            or WorkItemStatus.Uploading or WorkItemStatus.Downloading).ToList();

        var queueItems = allItems.Where(w => w.Status is not WorkItemStatus.Processing
            and not WorkItemStatus.Uploading and not WorkItemStatus.Downloading).ToList();

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<WorkItemStatus>(status, true, out var filterStatus))
            queueItems = queueItems.Where(w => w.Status == filterStatus).ToList();

        queueItems.Sort((a, b) =>
        {
            int StatusPriority(WorkItemStatus s) => s switch
            {
                WorkItemStatus.Pending   => 0,
                WorkItemStatus.Completed => 1,
                WorkItemStatus.Failed    => 2,
                WorkItemStatus.Cancelled => 3,
                _                        => 4
            };
            int cmp = StatusPriority(a.Status).CompareTo(StatusPriority(b.Status));
            return cmp != 0 ? cmp : b.Bitrate.CompareTo(a.Bitrate);
        });

        var total = queueItems.Count;
        queueItems = queueItems.Skip(skip).ToList();
        if (limit.HasValue) queueItems = queueItems.Take(limit.Value).ToList();
        return new JsonResult(new { items = queueItems, total, processing = processingItems });
    }

    /// <summary> Returns aggregate counts of work items by status. </summary>
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var workItems = _transcodingService.GetAllWorkItems();
        return new JsonResult(new
        {
            pending    = workItems.Count(w => w.Status == WorkItemStatus.Pending),
            processing = workItems.Count(w => w.Status is WorkItemStatus.Processing
                                                  or WorkItemStatus.Uploading or WorkItemStatus.Downloading),
            completed  = workItems.Count(w => w.Status == WorkItemStatus.Completed),
            failed     = workItems.Count(w => w.Status == WorkItemStatus.Failed),
            total      = workItems.Count
        });
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

    /// <summary> Re-queues a previously failed file for encoding. </summary>
    /// <param name="request"> Contains the file path to retry. </param>
    [HttpPost("retry")]
    public async Task<IActionResult> Retry([FromBody] RetryRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.FilePath)) return BadRequest("File path is required");
        await _transcodingService.RetryFileAsync(request.FilePath);
        return new JsonResult(new { success = true });
    }

    /// <summary> Returns all files recorded as failed in the persistent database. </summary>
    [HttpGet("failed")]
    public async Task<IActionResult> GetFailed()
    {
        var files = await _mediaFileRepo.GetFailedFilesAsync();
        return new JsonResult(files);
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
