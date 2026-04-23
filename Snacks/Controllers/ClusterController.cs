using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    public ClusterController(
        ClusterService clusterService,
        IntegrationService integrationService,
        IHubContext<TranscodingHub> hubContext)
    {
        _clusterService     = clusterService;
        _integrationService = integrationService;
        _hubContext         = hubContext;
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
                currentJobId = _clusterService.GetCurrentRemoteJobId(),
                progress = _clusterService.GetCurrentRemoteJobProgress(),
                completedJobId = _clusterService.GetCompletedJobId(),
                receivingJobId = _clusterService.GetReceivingJobId(),
                diskSpace = capabilities.AvailableDiskSpaceBytes,
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
            if (body.TryGetProperty("completion", out var completionProp) &&
                completionProp.TryGetProperty("noSavings", out var noSavingsProp))
                noSavings = noSavingsProp.GetBoolean();

            var config = _clusterService.GetConfig();
            var scheme = config.UseHttps ? "https" : "http";
            var nodeBaseUrl = $"{scheme}://{node.IpAddress}:{node.Port}";
            _ = Task.Run(() => _clusterService.HandleRemoteCompletionAsync(jobId, nodeBaseUrl, noSavings));
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

            try
            {
                // Mark this node as busy receiving so that heartbeat reports the correct state.
                _clusterService.SetReceivingJob(jobId);

                var rawFileName = Request.Headers["X-Original-FileName"].FirstOrDefault() ?? "input.mkv";
                try { rawFileName = Uri.UnescapeDataString(rawFileName); } catch (UriFormatException) { }
                var fileName = Path.GetFileName(rawFileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "input.mkv";
                var tempDir = _clusterService.GetNodeTempDirectory(jobId);
                var filePath = Path.Combine(tempDir, fileName);

                long offset = 0;
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
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
                    {
                        id = jobId,
                        fileName,
                        status = WorkItemStatus.Uploading,
                        progress = 0,
                        remoteJobPhase = "Uploading",
                        transferProgress = percent,
                        assignedNodeName = "master",
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

                        // Clean up metadata file
                        try { System.IO.File.Delete(metadataPath); } catch { }

                        if (!started)
                        {
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

                var files = Directory.GetFiles(tempDir)
                    .Where(f => !f.Contains("[snacks]") && !Path.GetFileName(f).StartsWith("_"))
                    .ToArray();
                if (files.Length == 0)
                {
                    Response.Headers["X-Received-Bytes"] = "0";
                    return Ok();
                }

                var fileInfo = new FileInfo(files[0]);
                Response.Headers["X-Received-Bytes"] = fileInfo.Length.ToString();
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
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
            {
                id = jobId,
                fileName = Path.GetFileName(outputPath).Replace(" [snacks]", ""),
                status = WorkItemStatus.Downloading,
                progress = 100,
                remoteJobPhase = "Downloading",
                transferProgress = percent,
                assignedNodeName = "master",
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

    }
