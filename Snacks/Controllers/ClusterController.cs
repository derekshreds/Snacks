using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using Snacks.Services;
using System.Text.Json;

namespace Snacks.Controllers;

/// <summary>
///     REST API controller for inter-node communication in the distributed encoding cluster.
///     All endpoints are protected by <see cref="ClusterAuthFilter"/>, which validates the
///     <c>X-Snacks-Secret</c> header against the configured shared secret. Covers node
///     discovery, heartbeat monitoring, job lifecycle, chunked file transfer with resume,
///     and graceful shutdown.
/// </summary>
[Route("api/cluster")]
[ApiController]
[ServiceFilter(typeof(ClusterAuthFilter))]
public sealed class ClusterController : ControllerBase
{
    private readonly ClusterService              _clusterService;
    private readonly IntegrationService          _integrationService;
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly JsonSerializerOptions       _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     Initializes the controller with the cluster coordination service and the SignalR hub
    ///     context used to push real-time transfer progress to the local UI.
    /// </summary>
    /// <param name="clusterService"> The cluster coordination service. </param>
    /// <param name="integrationService"> Source of master-side integration credentials served to workers. </param>
    /// <param name="hubContext"> SignalR hub context for pushing transfer progress to the UI. </param>
    private readonly EncodeHistoryRepository      _historyRepo;

    public ClusterController(
        ClusterService clusterService,
        IntegrationService integrationService,
        IHubContext<TranscodingHub> hubContext,
        EncodeHistoryRepository historyRepo)
    {
        _clusterService     = clusterService;
        _integrationService = integrationService;
        _hubContext         = hubContext;
        _historyRepo        = historyRepo;
    }

    /******************************************************************
     *  Discovery and Handshake
     ******************************************************************/

    /// <summary>
    ///     Performs a bidirectional handshake between two nodes. The sender supplies its own
    ///     node info, and the receiver registers it and returns its own, establishing mutual
    ///     awareness for cluster coordination.
    /// </summary>
    /// <param name="senderNode"> The node initiating the handshake. </param>
    /// <returns> The receiving node's info. </returns>
    [HttpPost("handshake")]
    public IActionResult Handshake([FromBody] ClusterNode senderNode)
    {
        // Override the sender's self-reported IP with the actual source address of
        // the TCP connection. A peer running in a container (e.g. Docker Desktop on
        // Windows) may advertise an internal VM IP that is unreachable from here;
        // the address we actually received the request from is, by definition,
        // routable back to it.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            var ipv4 = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
            senderNode.IpAddress = ipv4.ToString();
        }

        var (accepted, rejectReason) = _clusterService.RegisterOrUpdateNode(senderNode, fromHandshake: true);
        if (!accepted)
            return Conflict(new { error = rejectReason });
        var selfNode = _clusterService.BuildSelfNode();
        return Ok(selfNode);
    }

    /// <summary>
    ///     Force-resets a worker node by cancelling all active jobs and clearing stale state.
    ///     Primarily used by the master's automated resilience, but exposed for internal use.
    /// </summary>
    [HttpPost("nodes/{nodeId}/reset")]
    public async Task<IActionResult> ResetNode(string nodeId)
    {
        try
        {
            await _clusterService.ResetNodeAsync(nodeId);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Returns the current status of this node, including its ID, online/busy/paused
    ///     state, active job progress, available disk space, and hardware capabilities.
    ///     Called periodically by the master to monitor node health.
    /// </summary>
    [HttpGet("heartbeat")]
    public IActionResult Heartbeat()
    {
        var config = _clusterService.GetConfig();
        var isPaused = _clusterService.IsNodePaused;
        string status = isPaused ? "paused" : _clusterService.IsProcessingRemoteJob() ? "busy" : "online";
        var capabilities = _clusterService.GetCapabilities();
        return Ok(new
        {
            nodeId = config.NodeId,
            status,
            isPaused,
            // Per-slot tracking. activeJobs is one entry per occupied slot
            // (worker is encoding right now); completedJobIds are jobs whose
            // output is sitting on the worker waiting for the master to pull
            // it; receivingJobIds are jobs whose source file is mid-upload.
            activeJobs      = _clusterService.GetActiveJobs(),
            completedJobIds = _clusterService.GetCompletedJobIds(),
            receivingJobIds = _clusterService.GetReceivingJobIds(),
            diskSpace       = capabilities.AvailableDiskSpaceBytes,
            capabilities
        });
    }

    /// <summary>
    ///     Returns the hardware and software capabilities of this node. Used by the master
    ///     for intelligent job dispatch.
    /// </summary>
    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        return Ok(_clusterService.GetCapabilities());
    }

    /// <summary>
    ///     Returns the master's full cluster view — the list of all nodes the
    ///     master is currently tracking plus the master-side per-node settings
    ///     (concurrency overrides, 4K routing flags). Workers proxy this through
    ///     their own admin status endpoint so a browser viewing a worker's UI
    ///     sees the same accurate cluster state the master sees: true effective
    ///     concurrency caps, slot fill, busy/online status, and version of
    ///     every peer node — instead of the worker's stale local
    ///     <c>_nodes</c> map (which only contains entries the worker discovered
    ///     directly and is never reconciled against the master's heartbeats).
    /// </summary>
    [HttpGet("cluster-state")]
    public IActionResult GetClusterState()
    {
        // The master's own _nodes only contains peers; include BuildSelfNode
        // here so a worker proxying this endpoint sees the master in its
        // cluster view too. Without it, the worker UI's remote-card list
        // would silently drop the master when the proxy kicks in.
        //
        // BuildSelfNode hardcodes Status=Online and leaves ActiveJobs empty
        // — fine for handshake (the master's status is computed master-side
        // from active-job count) but wrong for cluster-state, where workers
        // render this entry directly as a node card. Stamp the runtime state
        // here so the master's card on a worker UI shows the same Busy /
        // Paused status and per-device slot fill the master's own UI shows.
        var self = _clusterService.BuildSelfNode();
        self.ActiveJobs     = _clusterService.GetEnrichedSelfActiveJobs();
        self.IsPaused       = !_clusterService.IsLocalEncodingEnabled;
        self.Status         = self.IsPaused
            ? Models.NodeStatus.Paused
            : (self.ActiveJobs.Count > 0 ? Models.NodeStatus.Busy : Models.NodeStatus.Online);
        self.CompletedJobs  = _clusterService.LocalCompletedJobs;
        self.FailedJobs     = _clusterService.LocalFailedJobs;
        // Master is not in _nodes (RefreshOffScheduleFlags only walks peers),
        // so workers proxying this endpoint would otherwise always see the
        // master's card without an Off-schedule badge. Stamp it here.
        self.OffSchedule    = !_clusterService.IsNodeWithinScheduleById(self.NodeId);

        var nodes = _clusterService.GetNodes().ToList();
        if (!nodes.Any(n => n.NodeId == self.NodeId)) nodes.Add(self);

        return Ok(new
        {
            nodes,
            nodeSettings = _clusterService.GetNodeSettingsConfig(),
        });
    }

    /// <summary>
    ///     Returns the master's current integration credentials (Plex / Jellyfin / Sonarr /
    ///     Radarr / TVDB / TMDb) so a worker can use them during original-language lookups
    ///     and sidecar OCR. Workers pull this endpoint before every encode.
    ///
    ///     <para>This endpoint is additionally protected by
    ///     <see cref="LocalNetworkOnlyFilter"/> which enforces a LAN-range source IP and a
    ///     match against a currently-connected <see cref="ClusterNode"/>. Credentials must
    ///     never leave the cluster, so the three filters — shared secret, LAN-only, and
    ///     connected-node allowlist — all have to pass.</para>
    ///
    ///     <para>The response omits <c>SharedSecret</c> (cluster-level, not an integration)
    ///     and forces <c>RescanOnComplete</c> to <c>false</c> — rescans are triggered centrally
    ///     by the master.</para>
    /// </summary>
    [HttpGet("integrations")]
    [ServiceFilter(typeof(LocalNetworkOnlyFilter))]
    public IActionResult GetIntegrations()
    {
        var source = _integrationService.GetConfig();

        // Copy without RescanOnComplete — workers never trigger rescans themselves.
        var payload = new IntegrationConfig
        {
            Plex = new MediaServerIntegration
            {
                BaseUrl          = source.Plex.BaseUrl,
                Token            = source.Plex.Token,
                Enabled          = source.Plex.Enabled,
                RescanOnComplete = false,
            },
            Jellyfin = new MediaServerIntegration
            {
                BaseUrl          = source.Jellyfin.BaseUrl,
                Token            = source.Jellyfin.Token,
                Enabled          = source.Jellyfin.Enabled,
                RescanOnComplete = false,
            },
            Sonarr = new ArrIntegration
            {
                BaseUrl = source.Sonarr.BaseUrl,
                ApiKey  = source.Sonarr.ApiKey,
                Enabled = source.Sonarr.Enabled,
            },
            Radarr = new ArrIntegration
            {
                BaseUrl = source.Radarr.BaseUrl,
                ApiKey  = source.Radarr.ApiKey,
                Enabled = source.Radarr.Enabled,
            },
            Tvdb = new TvdbIntegration
            {
                ApiKey  = source.Tvdb.ApiKey,
                Pin     = source.Tvdb.Pin,
                Enabled = source.Tvdb.Enabled,
            },
            Tmdb = new TmdbIntegration
            {
                ApiKey  = source.Tmdb.ApiKey,
                Enabled = source.Tmdb.Enabled,
            },
        };
        return new JsonResult(payload);
    }

    /// <summary> Returns the list of all known nodes in the cluster. Only available on master nodes. </summary>
    [HttpGet("nodes")]
    public IActionResult GetNodes()
    {
        return Ok(_clusterService.GetNodes());
    }

    /******************************************************************
     *  Job Lifecycle
     ******************************************************************/

    /// <summary>
    ///     Registers job metadata before a file upload begins.
    ///     Called once by the master before the first chunk, so the worker
    ///     knows what encoding options to apply once the upload completes.
    /// </summary>
    /// <param name="jobId"> The job ID being registered. </param>
    /// <param name="metadata"> Job metadata for autonomous encoding. </param>
    [HttpPost("files/{jobId}/metadata")]
    public IActionResult RegisterMetadata(string jobId, [FromBody] JobMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.JobId) || metadata.JobId != jobId)
            return BadRequest(new { error = "Job ID mismatch" });

        // Store metadata in temp directory as a JSON file for later retrieval.
        var tempDir = _clusterService.GetNodeTempDirectory(jobId);
        var metadataPath = Path.Combine(tempDir, "_metadata.json");
        try
        {
            System.IO.File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            // Record the assigned device so the worker self-card's per-device
            // chip counts this slot as occupied during the upload phase, not
            // only once encoding actually starts.
            _clusterService.RegisterReceivingDevice(jobId, metadata.DeviceId);
            Console.WriteLine($"Cluster: Registered metadata for job {jobId}");
            return Ok(new { registered = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to register metadata: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary> Pauses or resumes this node. When paused, the node will not accept new job offers. </summary>
    /// <param name="body"> JSON object with a <c>paused</c> boolean property. </param>
    [HttpPost("pause")]
    public IActionResult SetPaused([FromBody] JsonElement body)
    {
        var paused = body.GetProperty("paused").GetBoolean();
        _clusterService.SetNodePaused(paused);
        return Ok(new { paused = _clusterService.IsNodePaused });
    }

    /// <summary> Cancels a running job on this node, killing the FFmpeg process and cleaning up temp files. </summary>
    /// <param name="jobId"> The job ID to cancel. </param>
    [HttpDelete("jobs/{jobId}")]
    public IActionResult CancelJob(string jobId)
    {
        _clusterService.CancelRemoteJob(jobId);
        return Ok(new { cancelled = true });
    }

    /// <summary> Reports encoding progress from a worker node to the master. </summary>
    /// <param name="jobId"> The job being reported on. </param>
    /// <param name="progress"> Progress data including percentage and optional log lines. </param>
    [HttpPost("jobs/{jobId}/progress")]
    public async Task<IActionResult> ReportProgress(string jobId, [FromBody] JobProgress progress)
    {
        progress.JobId = jobId;
        await _clusterService.HandleRemoteProgressAsync(jobId, progress);
        return Ok();
    }

    /// <summary>
    ///     Lookup endpoint for workers to ask the master "do you still have this job tracked?"
    ///     Always returns 200 with <c>{ tracked, phase, recovering }</c> so a real 404 from
    ///     this URL means "endpoint doesn't exist on this master" (i.e. talking to an older
    ///     build) and the worker can fall back to its pre-probe retry behavior instead of
    ///     dropping a legitimate pending completion.
    ///
    ///     <para><c>recovering: true</c> tells the worker the master is still rebuilding
    ///     <c>_remoteJobs</c> from the DB after a restart — workers must NOT drop on
    ///     <c>tracked: false</c> in that window because the job may simply not have been
    ///     re-attached yet.</para>
    /// </summary>
    /// <param name="jobId"> The job ID to look up. </param>
    [HttpGet("jobs/{jobId}")]
    public IActionResult LookupJob(string jobId)
    {
        var phase = _clusterService.GetRemoteJobPhase(jobId);
        return Ok(new
        {
            jobId,
            tracked    = phase != null,
            phase      = phase,
            recovering = !_clusterService.RecoveryCompleteTask.IsCompleted,
        });
    }

    /// <summary>
    ///     Reports that encoding completed on a worker node. The master initiates the output
    ///     download in the background and returns immediately so the worker's POST does not time
    ///     out. The node URL is derived from the registered node's IP rather than the request
    ///     body to prevent SSRF attacks.
    /// </summary>
    /// <param name="jobId"> The completed job ID. </param>
    /// <param name="body"> Optional JSON body containing the <c>completion</c> payload. </param>
    [HttpPost("jobs/{jobId}/complete")]
    public IActionResult ReportCompletion(string jobId, [FromBody] JsonElement body)
    {
        var nodes = _clusterService.GetNodes();
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var node = nodes.FirstOrDefault(n =>
            n.IpAddress == remoteIp || n.IpAddress == HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString());

        if (node == null)
            return Unauthorized(new { error = "Unknown node" });

        bool noSavings = false;
        bool videoCopy = false;
        if (body.TryGetProperty("completion", out var completionProp))
        {
            if (completionProp.TryGetProperty("noSavings", out var noSavingsProp))
                noSavings = noSavingsProp.GetBoolean();
            if (completionProp.TryGetProperty("videoCopy", out var videoCopyProp))
                videoCopy = videoCopyProp.GetBoolean();
        }

        var config = _clusterService.GetConfig();
        var scheme = config.UseHttps ? "https" : "http";
        var nodeBaseUrl = $"{scheme}://{node.IpAddress}:{node.Port}";
        _ = Task.Run(() => _clusterService.HandleRemoteCompletionAsync(jobId, nodeBaseUrl, noSavings, videoCopy));
        return Ok();
    }

    /// <summary>
    ///     Reports that encoding failed on a worker node. The master will re-queue the job or
    ///     mark it as permanently failed depending on the retry count.
    /// </summary>
    /// <param name="jobId"> The failed job ID. </param>
    /// <param name="body"> JSON object with an optional <c>errorMessage</c> property. </param>
    [HttpPost("jobs/{jobId}/failed")]
    public async Task<IActionResult> ReportFailure(string jobId, [FromBody] JsonElement body)
    {
        string? errorMessage = null;
        if (body.TryGetProperty("errorMessage", out var msgProp))
            errorMessage = msgProp.GetString();

        await _clusterService.HandleRemoteFailureAsync(jobId, errorMessage);
        return Ok();
    }

    /******************************************************************
     *  File Transfer
     ******************************************************************/

    /// <summary>
    ///     Receives a chunk of a source file from the master for encoding. Supports chunked
    ///     upload with resume via <c>Range</c> headers, per-chunk SHA256 hash verification,
    ///     truncation of partial chunks on resume, and incremental hash computation to avoid
    ///     buffering the entire chunk in memory.
    /// </summary>
    /// <param name="jobId"> The job ID this file belongs to. </param>
    /// <returns> JSON with the total received byte count and whether the chunk hash matched. </returns>
    [HttpPut("files/{jobId}")]
    [RequestSizeLimit(75_000_000)] // 75MB — chunks are 50MB with headroom
    public async Task<IActionResult> ReceiveFile(string jobId)
    {
        // Cancel any in-flight request for this job — the old handler's body-read will
        // throw OperationCanceledException, releasing the file handle and semaphore.
        var receiveCt   = _clusterService.SwapReceiveCts(jobId);
        var receiveLock = _clusterService.GetReceiveLock(jobId);
        var ct = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted, receiveCt).Token;

        if (!await receiveLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return StatusCode(503, new { error = "Previous chunk still being written — try again" });

        // Hoisted so the FileNotFoundException catch below can report on-disk size and
        // the offset that was in flight when the file vanished — both belong in the
        // 409 body the master uses to re-align.
        string filePath = string.Empty;
        long   offset   = 0;

        try
        {
            // Mark this node as busy receiving so that heartbeat reports the correct state.
            _clusterService.SetReceivingJob(jobId);

            var tempDir = _clusterService.GetNodeTempDirectory(jobId);

            // Prefer the filename registered in _metadata.json so the HEAD/PUT pair
            // resolves to the same path. The X-Original-FileName header is the
            // historical source and is still honored as a fallback for any caller
            // that PUTs without first POSTing metadata.
            string fileName;
            if (!TryReadMetadataFileName(tempDir, out fileName!))
            {
                var rawFileName = Request.Headers["X-Original-FileName"].FirstOrDefault() ?? "input.mkv";
                try { rawFileName = Uri.UnescapeDataString(rawFileName); } catch (UriFormatException) { }
                fileName = Path.GetFileName(rawFileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "input.mkv";
            }

            filePath = Path.Combine(tempDir, fileName);

            var rangeHeader = Request.Headers["Range"].FirstOrDefault();
            if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
            {
                var rangeStart = rangeHeader.Replace("bytes=", "").Split('-')[0];
                long.TryParse(rangeStart, out offset);
            }

            // Guard: if offset > 0 the file MUST exist and be at least as large as offset.
            // Writing at a non-zero offset to a missing or too-short file creates a
            // zero-filled gap on NTFS, corrupting byte 0.
            if (offset > 0)
            {
                if (!System.IO.File.Exists(filePath))
                {
                    return StatusCode(409, new
                    {
                        error = $"File does not exist but offset is {offset} — re-query received bytes",
                        receivedBytes = 0L
                    });
                }

                var existingSize = new FileInfo(filePath).Length;
                if (offset > existingSize)
                {
                    return StatusCode(409, new
                    {
                        error = $"Offset {offset} beyond file size {existingSize} — re-query received bytes",
                        receivedBytes = existingSize
                    });
                }
            }

            var mode = offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;

            // Truncate file to the requested offset — discards any partially-written
            // chunk from a previous killed process before we append new data.
            // If the file is locked (previous handler still unwinding after cancellation),
            // wait briefly and retry rather than deleting the file which causes corruption.
            if (offset > 0 && System.IO.File.Exists(filePath))
            {
                var currentSize = new FileInfo(filePath).Length;
                if (currentSize > offset)
                {
                    for (int truncAttempt = 0; truncAttempt < 5; truncAttempt++)
                    {
                        try
                        {
                            using var truncStream = new FileStream(filePath, FileMode.Open, FileAccess.Write);
                            truncStream.SetLength(offset);
                            break;
                        }
                        catch (IOException) when (truncAttempt < 4)
                        {
                            // Previous handler may still be unwinding — wait for it
                            await Task.Delay(500, ct);
                        }
                        catch (IOException)
                        {
                            // Still locked after retries — reject this chunk so the master retries
                            return StatusCode(503, new { error = "File still locked after retries", size = currentSize });
                        }
                    }
                }
            }

            var expectedHash = Request.Headers["X-Chunk-Hash"].FirstOrDefault();
            using var incrementalHash = string.IsNullOrEmpty(expectedHash)
                ? null
                : System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

            var fileStream = await OpenFileWithRetryAsync(filePath, mode, ct);
            if (fileStream == null)
                return StatusCode(503, new { error = "File locked — previous write still unwinding" });

            using (fileStream)
            {
                if (offset > 0)
                    fileStream.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    incrementalHash?.AppendData(buffer.AsSpan(0, read));
                }
            }

            bool hashValid = true;
            if (incrementalHash != null && !string.IsNullOrEmpty(expectedHash))
            {
                var actualHash = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLower();
                hashValid = actualHash == expectedHash;
                if (!hashValid)
                {
                    Console.WriteLine($"Cluster: Chunk hash mismatch for {fileName} at offset {offset}");
                    // Truncate back to the offset to discard corrupt data
                    using var fixStream = new FileStream(filePath, FileMode.Open, FileAccess.Write);
                    fixStream.SetLength(offset);
                }
            }

            var fileSize = new FileInfo(filePath).Length;
            Response.Headers["X-Hash-Match"] = hashValid.ToString().ToLower();

            // Diagnostic: verify file header hasn't been corrupted after this chunk write.
            // Report the first 4 bytes in the response so the master can detect corruption.
            try
            {
                using var verifyStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var headerBytes = new byte[4];
                _ = await verifyStream.ReadAsync(headerBytes);
                var headerHex = Convert.ToHexString(headerBytes).ToLower();
                Response.Headers["X-File-Header"] = headerHex;

                if (headerBytes[0] == 0x00 && headerBytes[1] == 0x00 &&
                    headerBytes[2] == 0x00 && headerBytes[3] == 0x00)
                    Response.Headers["X-Header-Corrupt"] = "true";
            }
            catch { }

            var totalSizeHeader = Request.Headers["X-Total-Size"].FirstOrDefault();
            long.TryParse(Request.Headers["X-Bitrate"].FirstOrDefault(), out var bitrate);
            double.TryParse(Request.Headers["X-Duration"].FirstOrDefault(), out var duration);

            if (long.TryParse(totalSizeHeader, out var totalSize) && totalSize > 0)
            {
                var percent = (int)(fileSize * 100 / totalSize);
                // assignedNodeName is intentionally omitted: this broadcast goes
                // to the worker's own hub, where the work item is being processed
                // locally. The "Processing on remote node X" badge would mislabel
                // it (the worker is the processor, not "master").
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
                {
                    id = jobId,
                    fileName,
                    status = WorkItemStatus.Uploading,
                    progress = 0,
                    remoteJobPhase = "Uploading",
                    transferProgress = percent,
                    size = totalSize,
                    bitrate,
                    length = duration,
                    createdAt = DateTime.UtcNow
                });

                // Check if upload is complete and trigger autonomous encoding
                if (fileSize >= totalSize)
                {
                    var metadataPath = Path.Combine(tempDir, "_metadata.json");
                    if (!System.IO.File.Exists(metadataPath))
                    {
                        return StatusCode(500, new { error = $"Metadata file missing at {metadataPath}", size = fileSize });
                    }

                    JobMetadata? metadata;
                    try
                    {
                        var metadataJson = System.IO.File.ReadAllText(metadataPath);
                        metadata = JsonSerializer.Deserialize<JobMetadata>(metadataJson, _jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        return StatusCode(500, new { error = $"Metadata deserialization failed: {ex.Message}", size = fileSize });
                    }

                    if (metadata == null)
                    {
                        return StatusCode(500, new { error = "Metadata deserialized to null", size = fileSize });
                    }

                    var (started, rejectReason) = await _clusterService.StartAutonomousEncodingAsync(jobId, metadata, filePath);

                    if (started)
                    {
                        // Replace the synthetic 100% Uploading frame with a clean
                        // Processing/Encoding handover so the worker's local UI
                        // doesn't stay stuck at "Uploading 100%" through the OCR
                        // pre-pass — there is no other broadcast on the worker's
                        // hub for this job until encode completion otherwise.
                        // assignedNodeName is intentionally omitted: this worker
                        // is processing the job locally, so the "Processing on
                        // remote node X" badge would mislabel it.
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
                        {
                            id               = jobId,
                            fileName,
                            status           = WorkItemStatus.Processing,
                            progress         = 0,
                            remoteJobPhase   = "Encoding",
                            transferProgress = 0,
                            size             = totalSize,
                            bitrate,
                            length           = duration,
                            createdAt        = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        // Clear the synthetic 100% Uploading card we broadcast above —
                        // encoding will never start for this id, so no follow-up
                        // WorkItemUpdated would ever overwrite it. A non-transfer status
                        // also trips the UI's throttled refresh, which reconciles the
                        // orphan against the server's authoritative work-item list.
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
                        {
                            id               = jobId,
                            fileName,
                            status           = WorkItemStatus.Cancelled,
                            progress         = 0,
                            remoteJobPhase   = (string?)null,
                            transferProgress = 0,
                            completedAt      = DateTime.UtcNow,
                            size             = 0L,
                            bitrate          = 0L,
                            length           = 0.0,
                            createdAt        = DateTime.UtcNow
                        });
                        return StatusCode(503, new { error = $"Node rejected encoding: {rejectReason}", size = fileSize });
                    }
                }
            }

            return Ok(new { received = true, size = fileSize, hashValid });
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — silently release the lock so the next request proceeds.
            return StatusCode(499, new { error = "Client disconnected" });
        }
        catch (FileNotFoundException fnfEx)
        {
            // Source file vanished mid-handler — happens when something on the box
            // (cleanup, antivirus, Spotlight quarantine, an orphan handler) removes the
            // partial file between the initial File.Exists guard and a later
            // FileInfo.Length / FileMode.Open call. Returning 500 here drops us into the
            // master's transient-retry loop which loses the resume context; surface
            // the actual on-disk size as a 409 instead so the master's offset-mismatch
            // path re-aligns to wherever the file truly is.
            long currentSize = 0;
            try { if (System.IO.File.Exists(filePath)) currentSize = new FileInfo(filePath).Length; }
            catch { }
            Console.WriteLine(
                $"Cluster: ReceiveFile FileNotFound for job {jobId} at offset {offset}: " +
                $"{fnfEx.Message} — returning 409 with {currentSize} bytes for resume");
            return StatusCode(409, new
            {
                error         = "File disappeared during write — re-query received bytes",
                receivedBytes = currentSize
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: ReceiveFile error for job {jobId}: {ex.Message}");
            // Don't clear receiving state on transient errors — the master will retry
            // and ExpireStaleReceiving() handles the case where retries stop entirely.
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            receiveLock.Release();
        }
    }

    /// <summary>
    ///     Reports how many bytes of the source file have already been received, allowing the
    ///     master to resume an interrupted upload. The count is returned in the
    ///     <c>X-Received-Bytes</c> response header.
    /// </summary>
    /// <param name="jobId"> The job ID to check. </param>
    [HttpHead("files/{jobId}")]
    public IActionResult CheckFileStatus(string jobId)
    {
        try
        {
            var tempDir = _clusterService.GetNodeTempDirectory(jobId);
            if (!Directory.Exists(tempDir))
            {
                Response.Headers["X-Received-Bytes"] = "0";
                return Ok();
            }

            // Resolve the upload's filename from the metadata the master already
            // registered. The previous implementation enumerated the directory and
            // returned the first file that wasn't an encoded output or _metadata.json,
            // which on macOS APFS handed back whichever file happened to be first in
            // inode order — frequently a dot-file or stale artifact rather than the
            // source. The master would then see a tiny X-Received-Bytes value, send
            // chunk 0 with FileMode.Create, and truncate the in-flight upload back to
            // zero, producing a loop.
            if (!TryReadMetadataFileName(tempDir, out var fileName))
            {
                Response.Headers["X-Received-Bytes"] = "0";
                return Ok();
            }

            var filePath = Path.Combine(tempDir, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                Response.Headers["X-Received-Bytes"] = "0";
                return Ok();
            }

            long size;
            try { size = new FileInfo(filePath).Length; }
            catch (FileNotFoundException) { size = 0; }

            Response.Headers["X-Received-Bytes"] = size.ToString();
            return Ok();
        }
        catch
        {
            Response.Headers["X-Received-Bytes"] = "0";
            return Ok();
        }
    }

    /// <summary>
    ///     Serves a 50 MB chunk of the encoded output file for download by the master.
    ///     Supports <c>Range</c> headers for resume. Returns chunk and full-file SHA256 hashes
    ///     in response headers for end-to-end integrity verification.
    /// </summary>
    /// <param name="jobId"> The job ID whose output to download. </param>
    /// <returns> A binary chunk with <c>X-Chunk-Hash</c>, <c>X-Total-Size</c>, and <c>Content-Range</c> headers. </returns>
    [HttpGet("files/{jobId}/output")]
    public async Task<IActionResult> DownloadOutput(string jobId)
    {
        var outputPath = _clusterService.GetOutputFileForJob(jobId);
        if (outputPath == null || !System.IO.File.Exists(outputPath))
            return NotFound(new { error = "Output file not found" });

        var totalSize = new FileInfo(outputPath).Length;

        long offset = 0;
        var rangeHeader = Request.Headers["Range"].FirstOrDefault();
        if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
        {
            var rangeStart = rangeHeader.Replace("bytes=", "").Split('-')[0];
            if (long.TryParse(rangeStart, out var parsed) && parsed > 0 && parsed < totalSize)
                offset = parsed;
        }

        long chunkSize = 50L * 1024 * 1024; // 50MB
        var length = (int)Math.Min(chunkSize, Math.Max(totalSize - offset, 0));

        // Read chunk and compute hash using a small rolling buffer to avoid LOH allocation
        using var incrementalHash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var chunkStream = new MemoryStream(length);
        using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            var readBuffer = new byte[81920];
            int remaining = length;
            while (remaining > 0)
            {
                var read = await fs.ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, remaining)));
                if (read == 0) break;
                chunkStream.Write(readBuffer, 0, read);
                incrementalHash.AppendData(readBuffer.AsSpan(0, read));
                remaining -= read;
            }
        }
        length = (int)chunkStream.Length;
        var chunkHash = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLower();

        var percent = totalSize > 0 ? (int)((offset + length) * 100 / totalSize) : 0;
        // assignedNodeName is intentionally omitted: this broadcast goes to the
        // worker's own hub, where the work item was processed locally. The
        // "Processing on remote node X" badge would mislabel the source node.
        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
        {
            id = jobId,
            fileName = Path.GetFileName(outputPath).Replace(" [snacks]", ""),
            status = WorkItemStatus.Downloading,
            progress = 100,
            remoteJobPhase = "Downloading",
            transferProgress = percent,
            size = totalSize,
            bitrate = 0L,
            length = 0.0,
            createdAt = DateTime.UtcNow
        });

        Response.Headers["X-Total-Size"] = totalSize.ToString();
        Response.Headers["X-Chunk-Hash"] = chunkHash;
        Response.Headers["Content-Range"] = $"bytes {offset}-{offset + length - 1}/{totalSize}";

        chunkStream.Position = 0;
        return File(chunkStream, "application/octet-stream");
    }

    /// <summary>
    ///     Cleans up temp files for a completed or cancelled job. Called by the master after
    ///     it has successfully downloaded the encoded output.
    /// </summary>
    /// <param name="jobId"> The job ID to clean up. </param>
    [HttpDelete("files/{jobId}")]
    public async Task<IActionResult> CleanupFiles(string jobId)
    {
        _clusterService.CleanupJobFiles(jobId);

        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
        {
            id = jobId,
            fileName = "completed",
            status = "Completed",
            progress = 100,
            remoteJobPhase = (string?)null,
            completedAt = DateTime.UtcNow,
            size = 0L,
            bitrate = 0L,
            length = 0.0,
            createdAt = DateTime.UtcNow
        });

        return Ok(new { cleaned = true });
    }

    /******************************************************************
     *  Graceful Shutdown and Cleanup
     ******************************************************************/

    /// <summary>
    ///     Initiates a cluster shutdown. When <c>graceful</c> is <see langword="true"/>,
    ///     all nodes are paused and the operation waits up to <c>timeoutSeconds</c> for
    ///     active transfers to finish before stopping.
    /// </summary>
    /// <param name="body">
    ///     JSON with optional <c>graceful</c> (default: <see langword="true"/>) and
    ///     <c>timeoutSeconds</c> (default: 300) properties.
    /// </param>
    [HttpPost("shutdown")]
    public async Task<IActionResult> Shutdown([FromBody] JsonElement body)
    {
        bool graceful = true;
        int timeoutSeconds = 300;
        if (body.TryGetProperty("graceful", out var gracefulProp))
            graceful = gracefulProp.GetBoolean();
        if (body.TryGetProperty("timeoutSeconds", out var timeoutProp))
            timeoutSeconds = timeoutProp.GetInt32();

        await _clusterService.InitiateShutdownAsync(graceful, timeoutSeconds);
        return Ok(new { acknowledged = true, graceful });
    }

    /// <summary>
    ///     Manually triggers cleanup of remote job temp directories older than 24 hours,
    ///     reclaiming disk space on worker nodes.
    /// </summary>
    [HttpPost("cleanup-old-jobs")]
    public IActionResult CleanupOldJobs()
    {
        int ttlHours = 24;
        _clusterService.CleanupOldRemoteJobs(ttlHours);
        return Ok(new { cleaned = true });
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    /// <summary>
    ///     Opens a file for writing, retrying up to 5 times with 500ms delays if the
    ///     file is locked by a previous handler that is still unwinding after cancellation.
    /// </summary>
    private static async Task<FileStream?> OpenFileWithRetryAsync(
        string path, FileMode mode, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return new FileStream(path, mode, FileAccess.Write, FileShare.None, 81920);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(500, ct);
            }
            catch (IOException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    ///     Reads <c>_metadata.json</c> from the job's temp dir and extracts the
    ///     sanitized source filename. Used by the HEAD/PUT handlers as the source of
    ///     truth for "which file in the temp dir is the upload" — the previous
    ///     <see cref="Directory.GetFiles(string)"/> heuristic was non-deterministic on
    ///     APFS and let dot-files (<c>.DS_Store</c>, <c>.fseventsd</c>, Spotlight
    ///     metadata) shadow the real source, causing the master to think the upload
    ///     was at byte 0 and re-truncate the file on every dispatch.
    ///
    ///     <para>Opens with <see cref="FileShare.ReadWrite"/> so a concurrent
    ///     <c>RegisterMetadata</c> write doesn't crash the read; on any failure
    ///     (missing, locked, malformed) returns <see langword="false"/> and the
    ///     caller treats it as "no upload registered yet."</para>
    /// </summary>
    private bool TryReadMetadataFileName(string tempDir, out string fileName)
    {
        fileName = "input.mkv";
        var metadataPath = Path.Combine(tempDir, "_metadata.json");
        if (!System.IO.File.Exists(metadataPath)) return false;

        try
        {
            string json;
            using (var fs = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                json = reader.ReadToEnd();
            }

            var metadata = JsonSerializer.Deserialize<JobMetadata>(json, _jsonOptions);
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.FileName))
                return false;

            var raw = metadata.FileName;
            try { raw = Uri.UnescapeDataString(raw); } catch (UriFormatException) { }
            var sanitized = Path.GetFileName(raw);
            if (string.IsNullOrWhiteSpace(sanitized)) return false;

            fileName = sanitized;
            return true;
        }
        catch (IOException)                 { return false; }
        catch (JsonException)               { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /******************************************************************
     *  Dashboard mirror (RPC for worker dashboards)
     *
     *  Workers running in node mode have an empty local encode-history
     *  ledger — every completed encode is persisted on the master only.
     *  These endpoints surface the master's aggregations over the cluster
     *  shared-secret channel so a worker's <c>/api/dashboard/*</c> handlers
     *  can proxy through and render the same numbers a master would.
     *
     *  Endpoint shapes mirror <see cref="DashboardController"/> exactly.
     ******************************************************************/

    /// <summary> Lifetime totals for the hero strip. </summary>
    [HttpGet("dashboard/summary")]
    public async Task<IActionResult> DashboardSummary()
        => Ok(await _historyRepo.GetSummaryAsync());

    /// <summary> Daily savings rollup for the time-series chart. </summary>
    [HttpGet("dashboard/savings-over-time")]
    public async Task<IActionResult> DashboardSavingsOverTime([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _historyRepo.GetSavingsOverTimeAsync(days));
    }

    /// <summary> Per-device totals for the device utilization stripe. </summary>
    [HttpGet("dashboard/device-utilization")]
    public async Task<IActionResult> DashboardDeviceUtilization([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _historyRepo.GetDeviceUtilizationAsync(days));
    }

    /// <summary> Output codec mix donut data. </summary>
    [HttpGet("dashboard/codec-mix")]
    public async Task<IActionResult> DashboardCodecMix([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _historyRepo.GetCodecMixAsync(days));
    }

    /// <summary> Per-node throughput leaderboard. </summary>
    [HttpGet("dashboard/node-throughput")]
    public async Task<IActionResult> DashboardNodeThroughput([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _historyRepo.GetNodeThroughputAsync(days));
    }

    /// <summary> Most recent N completed encodes. </summary>
    [HttpGet("dashboard/recent")]
    public async Task<IActionResult> DashboardRecent([FromQuery] int limit = 25)
        => Ok(await _historyRepo.GetRecentAsync(limit));

    /// <summary> Top compression wins. </summary>
    [HttpGet("dashboard/top-savings")]
    public async Task<IActionResult> DashboardTopSavings([FromQuery] int limit = 10, [FromQuery] int days = 365)
    {
        limit = Math.Clamp(limit, 1, 100);
        days  = Math.Clamp(days, 1, 365);
        return Ok(await _historyRepo.GetTopSavingsAsync(limit, days));
    }

    /// <summary>
    ///     Wipes the master's encode-history ledger. Invoked by a worker's
    ///     Advanced settings tab when the user asks to clear dashboard data —
    ///     the worker proxies the request here because the master is the only
    ///     node that owns the ledger. Broadcasts <c>EncodeHistoryCleared</c>
    ///     so every connected client refreshes its dashboard view.
    /// </summary>
    [HttpDelete("dashboard/history")]
    public async Task<IActionResult> DashboardClearHistory()
    {
        var deleted = await _historyRepo.ClearAllAsync();
        await _hubContext.Clients.All.SendAsync("EncodeHistoryCleared");
        return Ok(new { success = true, deleted });
    }

}
