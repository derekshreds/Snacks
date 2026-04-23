using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Models.Requests;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Configuration and control endpoints for the auto-scan background service.
///     Manages watched directories, scan scheduling, exclusion rules, and manual triggers.
/// </summary>
[Route("api/auto-scan")]
[ApiController]
public sealed class AutoScanController : ControllerBase
{
    private readonly AutoScanService _autoScan;
    private readonly FileService     _fileService;

    public AutoScanController(AutoScanService autoScan, FileService fileService)
    {
        ArgumentNullException.ThrowIfNull(autoScan);
        ArgumentNullException.ThrowIfNull(fileService);
        _autoScan    = autoScan;
        _fileService = fileService;
    }

    /******************************************************************
     *  Configuration
     ******************************************************************/

    /// <summary> Returns the current auto-scan configuration. </summary>
    [HttpGet("config")]
    public IActionResult GetConfig() => new JsonResult(_autoScan.GetConfig());

    /// <summary> Enables or disables the auto-scan background service. </summary>
    /// <param name="request"> Contains the new enabled state. </param>
    [HttpPost("enabled")]
    public IActionResult SetEnabled([FromBody] AutoScanEnabledRequest request)
    {
        try
        {
            _autoScan.SetEnabled(request.Enabled);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary> Updates the scan interval. Accepts values between 1 and 1440 minutes. </summary>
    /// <param name="request"> Contains the new interval in minutes. </param>
    [HttpPost("interval")]
    public IActionResult SetInterval([FromBody] AutoScanIntervalRequest request)
    {
        if (request.IntervalMinutes < 1 || request.IntervalMinutes > 1440)
            return BadRequest("Interval must be between 1 and 1440 minutes");
        _autoScan.SetInterval(request.IntervalMinutes);
        return new JsonResult(new { success = true });
    }

    /******************************************************************
     *  Directories
     ******************************************************************/

    /// <summary>
    ///     Adds a directory to the auto-scan watch list. The path must exist and fall within
    ///     the allowed library root.
    /// </summary>
    /// <param name="request"> Contains the directory path to add. </param>
    [HttpPost("directories")]
    public IActionResult AddDirectory([FromBody] AutoScanDirectoryRequest request)
    {
        if (string.IsNullOrEmpty(request.Path)) return BadRequest("Path is required");
        if (!Directory.Exists(request.Path)) return BadRequest($"Directory does not exist: {request.Path}");
        if (!_fileService.IsPathAllowed(request.Path))
            return BadRequest("Directory is not within allowed library path");
        _autoScan.AddDirectory(request.Path);
        return new JsonResult(new { success = true });
    }

    /// <summary> Removes a directory from the auto-scan watch list. </summary>
    /// <param name="request"> Contains the directory path to remove. </param>
    [HttpDelete("directories")]
    public IActionResult RemoveDirectory([FromBody] AutoScanDirectoryRequest request)
    {
        if (string.IsNullOrEmpty(request.Path)) return BadRequest("Directory path is required");
        _autoScan.RemoveDirectory(request.Path);
        return new JsonResult(new { success = true });
    }

    /******************************************************************
     *  Scan Control
     ******************************************************************/

    /// <summary> Triggers an immediate scan without waiting for the scheduled interval. </summary>
    [HttpPost("trigger")]
    public IActionResult Trigger()
    {
        _ = Task.Run(() => _autoScan.TriggerScanNowAsync());
        return new JsonResult(new { success = true });
    }

    /// <summary> Clears the scan history so all files will be re-evaluated on the next scan. </summary>
    [HttpPost("clear-history")]
    public async Task<IActionResult> ClearHistory()
    {
        await _autoScan.ClearHistoryAsync();
        return new JsonResult(new { success = true });
    }

    /******************************************************************
     *  Exclusion Rules
     ******************************************************************/

    /// <summary> Returns the current file exclusion rules used during scans. </summary>
    [HttpGet("exclusions")]
    public IActionResult GetExclusions() => new JsonResult(_autoScan.GetConfig().ExclusionRules);

    /// <summary> Replaces the current exclusion rules and persists the change. </summary>
    /// <param name="rules"> The new exclusion rules to apply. </param>
    [HttpPost("exclusions")]
    public IActionResult SaveExclusions([FromBody] ExclusionRules rules)
    {
        _autoScan.UpdateExclusionRules(rules ?? new ExclusionRules());
        return new JsonResult(new { success = true });
    }
}
