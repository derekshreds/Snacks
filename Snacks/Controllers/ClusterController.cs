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
    /// <param name="hubContext"> SignalR hub context for pushing transfer progress to the UI. </param>
    public ClusterController(ClusterService clusterService, IHubContext<TranscodingHub> hubContext)
    {
        _clusterService = clusterService;
        _hubContext     = hubContext;
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
            _clusterService.RegisterOrUpdateNode(senderNode, fromHandshake: true);
            var selfNode = _clusterService.BuildSelfNode();
            return Ok(selfNode);
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
        ///     Offers a transcoding job to this node. The node accepts when it is not paused,
        ///     not already processing a job, and the source file is present in the temp directory.
        ///     Returns 409 Conflict if the offer is rejected.
        /// </summary>
        /// <param name="assignment"> Job details including file info and encoding options. </param>
        [HttpPost("jobs/offer")]
        public async Task<IActionResult> OfferJob([FromBody] JobAssignment assignment)
        {
            var accepted = await _clusterService.AcceptJobOfferAsync(assignment);
            if (!accepted)
                return Conflict(new { error = "Node is busy or source file not found" });

            return Ok(new { accepted = true, jobId = assignment.JobId });
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
        [HttpPost("jobs/{jobId}/complete")]
        public IActionResult ReportCompletion(string jobId)
        {
            var nodes = _clusterService.GetNodes();
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var node = nodes.FirstOrDefault(n =>
                n.IpAddress == remoteIp || n.IpAddress == HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString());

            if (node == null)
                return Unauthorized(new { error = "Unknown node" });

            var nodeBaseUrl = $"http://{node.IpAddress}:{node.Port}";
            _ = Task.Run(() => _clusterService.HandleRemoteCompletionAsync(jobId, nodeBaseUrl));
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
            try
            {
                // Mark this node as busy receiving so that heartbeat reports the correct state.
                _clusterService.SetReceivingJob(jobId);

                var rawFileName = Request.Headers["X-Original-FileName"].FirstOrDefault() ?? "input.mkv";
                var fileName = Path.GetFileName(rawFileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "input.mkv";
                var tempDir = _clusterService.GetNodeTempDirectory(jobId);
                var filePath = Path.Combine(tempDir, fileName);

                long offset = 0;
                var rangeHeader = Request.Headers["Range"].FirstOrDefault();
                if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
                {
                    var rangeStart = rangeHeader.Replace("bytes=", "").Split('-')[0];
                    if (long.TryParse(rangeStart, out offset) && offset > 0 && System.IO.File.Exists(filePath))
                    {
                        var existingSize = new FileInfo(filePath).Length;
                        if (offset > existingSize)
                            offset = 0;
                    }
                }

                // Fresh start (offset 0, no Range header) for a job that already has data —
                // truncate any existing files so we don't accumulate stale data from a previous attempt
                if (offset == 0 && rangeHeader == null && System.IO.File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                {
                    Console.WriteLine($"Cluster: Fresh upload start for job {jobId} — truncating existing {new FileInfo(filePath).Length} bytes");
                }

                var mode = offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;

                // Truncate file to the requested offset — discards any partially-written
                // chunk from a previous killed process before we append new data
                if (offset > 0 && System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var currentSize = new FileInfo(filePath).Length;
                        if (currentSize > offset)
                        {
                            using var truncStream = new FileStream(filePath, FileMode.Open, FileAccess.Write);
                            truncStream.SetLength(offset);
                        }
                    }
                    catch (IOException)
                    {
                        // File is locked by a zombie process — delete and start fresh
                        Console.WriteLine($"Cluster: File locked for job {jobId} — deleting and starting fresh");
                        try { System.IO.File.Delete(filePath); } catch { }
                        offset = 0;
                        mode = FileMode.Create;
                    }
                }

                var expectedHash = Request.Headers["X-Chunk-Hash"].FirstOrDefault();
                using var incrementalHash = string.IsNullOrEmpty(expectedHash)
                    ? null
                    : System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

                using (var fileStream = new FileStream(filePath, mode, FileAccess.Write, FileShare.None, 81920))
                {
                    if (offset > 0)
                        fileStream.Seek(offset, SeekOrigin.Begin);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await Request.Body.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
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
                        status = "Processing",
                        progress = 0,
                        remoteJobPhase = "Downloading",
                        transferProgress = percent,
                        assignedNodeName = "master",
                        size = totalSize,
                        bitrate,
                        length = duration,
                        createdAt = DateTime.UtcNow
                    });
                }

                return Ok(new { received = true, size = fileSize, hashValid });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: ReceiveFile error for job {jobId}: {ex.Message}");
                _clusterService.SetReceivingJob(null);
                return StatusCode(500, new { error = ex.Message });
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

                var files = Directory.GetFiles(tempDir).Where(f => !f.Contains("[snacks]")).ToArray();
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

            // Compute full file hash for end-to-end integrity verification (first request only)
            string? fullFileHash = null;
            if (offset == 0)
            {
                using var fullFs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fullHash = await System.Security.Cryptography.SHA256.HashDataAsync(fullFs);
                fullFileHash = Convert.ToHexString(fullHash).ToLower();
            }

            var percent = totalSize > 0 ? (int)((offset + length) * 100 / totalSize) : 0;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
            {
                id = jobId,
                fileName = Path.GetFileName(outputPath).Replace(" [snacks]", ""),
                status = "Processing",
                progress = 100,
                remoteJobPhase = "Uploading",
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
            if (fullFileHash != null)
                Response.Headers["X-File-Hash"] = fullFileHash;

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

    }

