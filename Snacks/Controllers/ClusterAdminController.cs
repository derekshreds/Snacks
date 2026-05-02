using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Models.Requests;
using Snacks.Services;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

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
    ///
    ///     <para>When this instance is running as a worker node, the
    ///     <c>nodes</c> list is proxied from the master's authoritative
    ///     <c>/api/cluster/cluster-state</c> endpoint instead of the worker's
    ///     local <c>_nodes</c> map. The worker only ever heartbeats with the
    ///     master directly, so its local map is missing peer workers and
    ///     contains stale concurrency caps and busy/online state. Fetching
    ///     from the master gives the browser viewing the worker's UI the
    ///     same view the master has — true effective concurrency, correct
    ///     per-device slot fill, accurate paused/online status, and the
    ///     version of every peer. On master-fetch failure the call falls
    ///     back to the worker's local view so the page still loads during
    ///     a transient master outage.</para>
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var config = _clusterService.GetConfig();

        IReadOnlyList<ClusterNode> nodes = _clusterService.GetNodes();
        if (config.Enabled && config.Role == "node")
        {
            // Prefer the background-refreshed cache (kept current by the
            // worker's heartbeat-tick poll of master's cluster-state). On
            // cold start the cache hasn't been populated yet — fall back
            // to a one-shot proxy fetch so the first page load is correct.
            var (cachedNodes, _) = _clusterService.GetCachedMasterClusterState();
            if (cachedNodes != null)
            {
                nodes = cachedNodes;
            }
            else
            {
                var proxied = await TryFetchMasterClusterStateAsync();
                if (proxied?.Nodes != null) nodes = proxied.Nodes;
            }
        }

        // localActiveJobs source: workers route encodes through the node-job
        // service, which tracks both encoding-in-progress and the
        // upload-in-flight phase. Using ClusterService.GetActiveJobs here on
        // workers makes the self-card chips reflect a slot the moment a file
        // starts uploading — not only once encoding begins.
        var localActive = config.Role == "node"
            ? _clusterService.GetActiveJobs()
            : _transcodingService.GetActiveLocalJobs();

        return new JsonResult(new
        {
            enabled              = config.Enabled,
            role                 = config.Role,
            nodeName             = config.NodeName,
            nodeId               = config.NodeId,
            selfVersion          = ClusterDiscoveryService.ClusterVersion,
            localEncodingEnabled = config.LocalEncodingEnabled,
            selfCapabilities     = _clusterService.GetCapabilities(),
            // Multi-slot self status: one ActiveJobInfo per local slot the
            // master is currently encoding on. Mirrors the activeJobs[] shape
            // surfaced on remote nodes so the dashboard renders the master's
            // self-card with the same per-device chips and progress bars.
            localActiveJobs      = localActive,
            localCompletedJobs   = _transcodingService.LocalCompletedJobs,
            localFailedJobs      = _transcodingService.LocalFailedJobs,
            nodeCount            = nodes.Count + 1,
            nodes                = nodes,
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

    /// <summary>
    ///     Returns all per-node encoding override configurations.
    ///
    ///     <para>On worker nodes the master's config is proxied through —
    ///     workers don't carry NodeSettings in their own state, and the UI
    ///     needs the master's overrides to render correct concurrency caps
    ///     on every node card. Falls back to the worker's empty local view
    ///     if the master can't be reached.</para>
    /// </summary>
    [HttpGet("node-settings")]
    public async Task<IActionResult> GetNodeSettings()
    {
        var config = _clusterService.GetConfig();
        if (config.Enabled && config.Role == "node")
        {
            var (_, cachedSettings) = _clusterService.GetCachedMasterClusterState();
            if (cachedSettings != null) return new JsonResult(cachedSettings);

            var proxied = await TryFetchMasterClusterStateAsync();
            if (proxied?.NodeSettings != null) return new JsonResult(proxied.NodeSettings);
        }
        return new JsonResult(_clusterService.GetNodeSettingsConfig());
    }

    /******************************************************************
     *  Master-state proxy (worker mode)
     ******************************************************************/

    private sealed class MasterClusterState
    {
        public List<ClusterNode>?    Nodes        { get; set; }
        public NodeSettingsConfig?   NodeSettings { get; set; }
    }

    /// <summary>
    ///     Fetches the master's full cluster view via the shared-secret
    ///     <c>/api/cluster/cluster-state</c> endpoint. Returns <see langword="null"/>
    ///     if the master URL isn't resolvable, the call fails, the response
    ///     isn't valid JSON, or the round trip exceeds the short timeout.
    ///     Callers fall back to the worker's local view in any of those cases
    ///     so the UI still loads during a master outage.
    /// </summary>
    private async Task<MasterClusterState?> TryFetchMasterClusterStateAsync()
    {
        var masterUrl = _clusterService.ResolveMasterUrl();
        if (string.IsNullOrEmpty(masterUrl)) return null;

        try
        {
            using var client = _clusterService.CreateClusterClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await client.GetAsync($"{masterUrl.TrimEnd('/')}/api/cluster/cluster-state");
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<MasterClusterState>(body, _jsonOpts);
        }
        catch
        {
            return null;
        }
    }

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

    /******************************************************************
     *  Integration Sync (worker-only UI)
     ******************************************************************/

    /// <summary>
    ///     Returns the integration-pull sync status for this worker. Used by the
    ///     "Integration Sync" tab that is only visible when the role is <c>node</c>.
    /// </summary>
    [HttpGet("integration-sync")]
    public IActionResult GetIntegrationSyncStatus()
    {
        var cfg = _clusterService.GetConfig();
        var lastAt = _clusterService.LastIntegrationSyncAt;
        return new JsonResult(new
        {
            masterUrl  = cfg.MasterUrl,
            lastSyncAt = lastAt?.ToString("o"),
            status     = _clusterService.LastIntegrationSyncStatus,
        });
    }

    /// <summary>
    ///     Triggers an on-demand integration-credentials pull from the master. Surfaces
    ///     the same code path the pre-encode hook uses; useful for verifying the
    ///     master connection without waiting for a queued job.
    /// </summary>
    [HttpPost("integration-sync/refresh")]
    public async Task<IActionResult> RefreshIntegrationSync()
    {
        await _clusterService.PullIntegrationsFromMasterAsync(HttpContext.RequestAborted);
        return new JsonResult(new
        {
            lastSyncAt = _clusterService.LastIntegrationSyncAt?.ToString("o"),
            status     = _clusterService.LastIntegrationSyncStatus,
        });
    }
}
