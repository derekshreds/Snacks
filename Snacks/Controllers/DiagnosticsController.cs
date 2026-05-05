using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Read-only operations diagnostics. Surfaces the persistent ops log written by
///     Serilog so the user can audit recent activity without ssh-ing into the host —
///     specifically aimed at the "queue items vanished overnight" case where every prior
///     diagnosis had to rely on guesswork because <c>Console.WriteLine</c> didn't survive
///     the process.
///
///     <para>Both endpoints accept an optional <c>nodeId</c> query parameter. When the
///     id matches the local node (or is omitted) the request is served from the local
///     <c>logs/</c> directory. When the id is a remote node the request is proxied to
///     that node's <c>/api/cluster/diagnostics/*</c> mirror over the cluster
///     shared-secret channel — same pattern <see cref="DashboardController"/> uses for
///     master-side aggregations.</para>
/// </summary>
[Route("api/diagnostics")]
[ApiController]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly FileService        _fileService;
    private readonly ClusterService     _clusterService;
    private readonly LogArchiveService  _logArchive;

    public DiagnosticsController(
        FileService fileService,
        ClusterService clusterService,
        LogArchiveService logArchive)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(clusterService);
        ArgumentNullException.ThrowIfNull(logArchive);
        _fileService    = fileService;
        _clusterService = clusterService;
        _logArchive     = logArchive;
    }

    /// <summary>
    ///     Returns the tail of the most recent operations log file under
    ///     <c>${SNACKS_WORK_DIR}/logs/snacks-*.log</c> for the node identified by
    ///     <paramref name="nodeId"/>, or the local node when omitted.
    /// </summary>
    /// <param name="lines">
    ///     Maximum number of lines to return from the end of the latest log file.
    ///     Clamped to [1, 5000] so a single request can't pin the host on a large log.
    /// </param>
    /// <param name="nodeId"> Target node id; omit (or pass the local id) for local logs. </param>
    [HttpGet("log")]
    public async Task<IActionResult> GetLogTail([FromQuery] int lines = 200, [FromQuery] string? nodeId = null)
    {
        var clamped = Math.Clamp(lines, 1, 5000);

        if (TryResolveRemote(nodeId, out var remote))
            return await ProxyTailAsync(remote!, clamped);

        var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");

        try
        {
            var (latest, tail) = _logArchive.ReadLatestLogTail(logsDir, clamped);
            if (latest == null || tail == null)
                return new JsonResult(new { logsDir, available = false, lines = Array.Empty<string>() });

            return new JsonResult(new
            {
                logsDir,
                available    = true,
                logFile      = latest.Name,
                lastWriteUtc = latest.LastWriteTimeUtc,
                lineCount    = tail.Length,
                lines        = tail,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Streams a ZIP of every <c>*.log</c> under the target node's logs
    ///     directory. Local node when <paramref name="nodeId"/> is omitted; otherwise
    ///     proxied to the remote node and re-streamed to the browser.
    /// </summary>
    [HttpGet("logs.zip")]
    public async Task<IActionResult> GetLogsZip([FromQuery] string? nodeId = null)
    {
        if (TryResolveRemote(nodeId, out var remote))
            return await ProxyZipAsync(remote!);

        var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");
        var config  = _clusterService.GetConfig();
        var stamp   = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safe    = string.Join('_', (config.NodeName ?? "node").Split(Path.GetInvalidFileNameChars()));

        // ZipArchive writes synchronously and ASP.NET Core blocks sync I/O on
        // Response.Body. Build into a MemoryStream first; the per-file 50 MB
        // skip in WriteLogsZip keeps the buffer bounded (~70 MB worst case
        // for 7 daily rolls + per-job logs).
        var buffer = new MemoryStream();
        _logArchive.WriteLogsZip(buffer, logsDir);
        buffer.Position = 0;
        return File(buffer, "application/zip", $"snacks-logs-{safe}-{stamp}.zip");
    }

    /// <summary>
    ///     Resolves a target node id to a remote <see cref="ClusterNode"/>, or returns
    ///     <c>false</c> when the request should be served locally (id omitted, equals
    ///     the local node, or doesn't match a known peer).
    /// </summary>
    private bool TryResolveRemote(string? nodeId, out ClusterNode? node)
    {
        node = null;
        if (string.IsNullOrEmpty(nodeId)) return false;

        var localId = _clusterService.GetConfig().NodeId;
        if (string.Equals(nodeId, localId, StringComparison.OrdinalIgnoreCase)) return false;

        node = _clusterService.GetNodes().FirstOrDefault(n =>
            string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        return node != null;
    }

    /// <summary>
    ///     Forwards a tail request to a remote node's <c>/api/cluster/diagnostics/log</c>.
    ///     Returns a 503 with diagnostic context when the node is offline or the call fails,
    ///     so the polling UI can keep its last good snapshot and recover automatically.
    /// </summary>
    private async Task<IActionResult> ProxyTailAsync(ClusterNode node, int lines)
    {
        if (node.Status == NodeStatus.Offline || node.Status == NodeStatus.Unreachable)
            return StatusCode(503, new
            {
                error    = "Node unreachable",
                nodeId   = node.NodeId,
                hostname = node.Hostname,
                lastSeen = node.LastHeartbeat,
            });

        try
        {
            var scheme = _clusterService.GetConfig().UseHttps ? "https" : "http";
            var url    = $"{scheme}://{node.IpAddress}:{node.Port}/api/cluster/diagnostics/log?lines={lines}";
            var client = _clusterService.CreateClusterClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.GetAsync(url);
            var body  = await resp.Content.ReadAsStringAsync();
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            Response.StatusCode = (int)resp.StatusCode;
            return Content(body, ctype);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new
            {
                error    = ex.Message,
                nodeId   = node.NodeId,
                hostname = node.Hostname,
                lastSeen = node.LastHeartbeat,
            });
        }
    }

    /// <summary>
    ///     Forwards a ZIP-export request to a remote node and streams the response body
    ///     straight through to the calling browser without buffering server-side.
    /// </summary>
    private async Task<IActionResult> ProxyZipAsync(ClusterNode node)
    {
        if (node.Status == NodeStatus.Offline || node.Status == NodeStatus.Unreachable)
            return StatusCode(503, new { error = "Node unreachable", nodeId = node.NodeId, hostname = node.Hostname });

        var scheme = _clusterService.GetConfig().UseHttps ? "https" : "http";
        var url    = $"{scheme}://{node.IpAddress}:{node.Port}/api/cluster/diagnostics/logs.zip";
        var client = _clusterService.CreateClusterClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message, nodeId = node.NodeId, hostname = node.Hostname });
        }

        try
        {
            if (!upstream.IsSuccessStatusCode)
            {
                var body  = await upstream.Content.ReadAsStringAsync();
                var ctype = upstream.Content.Headers.ContentType?.ToString() ?? "text/plain";
                Response.StatusCode = (int)upstream.StatusCode;
                return Content(body, ctype);
            }

            // Carry through the upstream filename so the saved zip carries the
            // remote node's hostname, not the master's.
            var disposition = upstream.Content.Headers.ContentDisposition?.ToString();
            if (!string.IsNullOrEmpty(disposition))
                Response.Headers["Content-Disposition"] = disposition;
            else
                Response.Headers["Content-Disposition"] =
                    $"attachment; filename=\"snacks-logs-{node.Hostname}.zip\"";

            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/zip";
            await upstream.Content.CopyToAsync(Response.Body);
            return new EmptyResult();
        }
        finally
        {
            upstream.Dispose();
        }
    }
}
