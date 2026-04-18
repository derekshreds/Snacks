using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Models.Requests;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Admin/UI endpoints for managing the cluster from the control panel.
///     Separate from <see cref="ClusterController"/>, which hosts the
///     shared-secret-authenticated inter-node RPC surface.
/// </summary>
[Route("api/cluster-admin")]
[ApiController]
public sealed class ClusterAdminController : ControllerBase
{
    private readonly ClusterService     _clusterService;
    private readonly TranscodingService _transcodingService;
    private readonly AutoScanService    _autoScanService;

    public ClusterAdminController(
        ClusterService clusterService,
        TranscodingService transcodingService,
        AutoScanService autoScanService)
    {
        ArgumentNullException.ThrowIfNull(clusterService);
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(autoScanService);
        _clusterService     = clusterService;
        _transcodingService = transcodingService;
        _autoScanService    = autoScanService;
    }

    /******************************************************************
     *  Cluster Config
     ******************************************************************/

    /// <summary>
    ///     Returns the current cluster configuration. The shared secret is omitted from the
    ///     response; its presence is indicated by the <c>hasSecret</c> flag.
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var config = _clusterService.GetConfig();
        return new JsonResult(new
        {
            config.Enabled,
            config.Role,
            config.NodeName,
            hasSecret = !string.IsNullOrEmpty(config.SharedSecret),
            config.AutoDiscovery,
            config.LocalEncodingEnabled,
            config.MasterUrl,
            config.NodeTempDirectory,
            config.ManualNodes,
            config.HeartbeatIntervalSeconds,
            config.NodeTimeoutSeconds,
            config.NodeId
        });
    }

    /// <summary>
    ///     Saves and applies updated cluster configuration. A shared secret is required
    ///     when enabling non-standalone cluster mode.
    /// </summary>
    /// <param name="config"> The new cluster configuration to apply. </param>
    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] ClusterConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(config.SharedSecret))
                config.SharedSecret = _clusterService.GetConfig().SharedSecret;

            if (config.Enabled && config.Role != "standalone" && string.IsNullOrEmpty(config.SharedSecret))
                return new JsonResult(new { success = false, error = "A shared secret is required to enable cluster mode." });

            await _clusterService.SaveConfigAndApplyAsync(config);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    /******************************************************************
     *  Cluster Status
     ******************************************************************/

    /// <summary>
    ///     Returns the current cluster operational status including node capabilities,
    ///     local job counters, and the list of connected nodes.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var config = _clusterService.GetConfig();
        return new JsonResult(new
        {
            enabled              = config.Enabled,
            role                 = config.Role,
            nodeName             = config.NodeName,
            nodeId               = config.NodeId,
            localEncodingEnabled = config.LocalEncodingEnabled,
            selfCapabilities     = _clusterService.GetCapabilities(),
            localCompletedJobs   = _transcodingService.LocalCompletedJobs,
            localFailedJobs      = _transcodingService.LocalFailedJobs,
            nodeCount            = _clusterService.GetNodes().Count + 1,
            nodes                = _clusterService.GetNodes()
        });
    }

    /// <summary> Returns the list of connected cluster worker nodes. </summary>
    [HttpGet("workers")]
    public IActionResult GetWorkers() => new JsonResult(_clusterService.GetNodes());

    /******************************************************************
     *  Node Management
     ******************************************************************/

    /// <summary> Remotely pauses or resumes encoding on a specific cluster node. </summary>
    /// <param name="request"> Node ID and the desired paused state. </param>
    [HttpPost("node-paused")]
    public async Task<IActionResult> SetNodePaused([FromBody] NodePauseRequest request)
    {
        try
        {
            await _clusterService.SetRemoteNodePausedAsync(request.NodeId, request.Paused);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    /// <summary> Enables or disables local encoding on the master node. </summary>
    /// <param name="request"> Contains the desired paused state for local encoding. </param>
    [HttpPost("local-encoding-paused")]
    public IActionResult SetLocalEncodingPaused([FromBody] PauseRequest request)
    {
        try
        {
            _clusterService.SetLocalEncodingEnabled(!request.Paused);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    /******************************************************************
     *  Node and Folder Settings
     ******************************************************************/

    /// <summary> Returns all per-node encoding override configurations. </summary>
    [HttpGet("node-settings")]
    public IActionResult GetNodeSettings() => new JsonResult(_clusterService.GetNodeSettingsConfig());

    /// <summary>
    ///     Saves encoding overrides for a specific cluster node. The <c>Only4K</c> and
    ///     <c>Exclude4K</c> flags are mutually exclusive.
    /// </summary>
    /// <param name="settings"> The node settings to save. </param>
    [HttpPost("node-settings")]
    public IActionResult SaveNodeSettings([FromBody] NodeSettings settings)
    {
        if (string.IsNullOrEmpty(settings.NodeId)) return BadRequest("NodeId is required");
        if (settings.Only4K == true && settings.Exclude4K == true)
            return BadRequest("Only4K and Exclude4K are mutually exclusive");
        _clusterService.SaveNodeSettings(settings);
        return new JsonResult(new { success = true });
    }

    /// <summary> Deletes the per-node encoding override for a specific cluster node. </summary>
    /// <param name="request"> Contains the node ID whose settings should be deleted. </param>
    [HttpDelete("node-settings")]
    public IActionResult DeleteNodeSettings([FromBody] DeleteNodeSettingsRequest request)
    {
        if (string.IsNullOrEmpty(request.NodeId)) return BadRequest("NodeId is required");
        _clusterService.DeleteNodeSettings(request.NodeId);
        return new JsonResult(new { success = true });
    }

    /// <summary> Saves per-folder encoding overrides for a watched directory. </summary>
    /// <param name="request"> Folder path and the encoder overrides to apply. </param>
    [HttpPost("folder-settings")]
    public IActionResult SaveFolderSettings([FromBody] SaveFolderSettingsRequest request)
    {
        if (string.IsNullOrEmpty(request.Path)) return BadRequest("Path is required");
        _autoScanService.SaveFolderSettings(request.Path, request.EncodingOverrides);
        return new JsonResult(new { success = true });
    }
}
