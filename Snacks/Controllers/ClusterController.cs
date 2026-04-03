using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using Snacks.Services;
using System.Text.Json;

namespace Snacks.Controllers
{
    [Route("api/cluster")]
    [ApiController]
    [ServiceFilter(typeof(ClusterAuthFilter))]
    public class ClusterController : ControllerBase
    {
        private readonly ClusterService _clusterService;
        private readonly IHubContext<TranscodingHub> _hubContext;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ClusterController(ClusterService clusterService, IHubContext<TranscodingHub> hubContext)
        {
            _clusterService = clusterService;
            _hubContext = hubContext;
        }

        // --- Discovery ---

        [HttpPost("handshake")]
        public IActionResult Handshake([FromBody] ClusterNode senderNode)
        {
            _clusterService.RegisterOrUpdateNode(senderNode, fromHandshake: true);
            var selfNode = _clusterService.BuildSelfNode();
            return Ok(selfNode);
        }

        [HttpGet("heartbeat")]
        public IActionResult Heartbeat()
        {
            var config = _clusterService.GetConfig();
            var isPaused = _clusterService.IsNodePaused;
            string status = isPaused ? "paused" : _clusterService.IsProcessingRemoteJob() ? "busy" : "online";
            return Ok(new
            {
                nodeId = config.NodeId,
                status,
                isPaused,
                currentJobId = _clusterService.GetCurrentRemoteJobId(),
                progress = _clusterService.GetCurrentRemoteJobProgress(),
                diskSpace = GetAvailableDiskSpace(),
                capabilities = _clusterService.GetCapabilities()
            });
        }

        [HttpGet("capabilities")]
        public IActionResult GetCapabilities()
        {
            return Ok(_clusterService.GetCapabilities());
        }

        [HttpGet("nodes")]
        public IActionResult GetNodes()
        {
            return Ok(_clusterService.GetNodes());
        }

        // --- Job lifecycle ---

        [HttpPost("jobs/offer")]
        public async Task<IActionResult> OfferJob([FromBody] JobAssignment assignment)
        {
            var accepted = await _clusterService.AcceptJobOfferAsync(assignment);
            if (!accepted)
                return Conflict(new { error = "Node is busy or source file not found" });

            return Ok(new { accepted = true, jobId = assignment.JobId });
        }

        [HttpPost("pause")]
        public IActionResult SetPaused([FromBody] JsonElement body)
        {
            var paused = body.GetProperty("paused").GetBoolean();
            _clusterService.SetNodePaused(paused);
            return Ok(new { paused = _clusterService.IsNodePaused });
        }

        [HttpDelete("jobs/{jobId}")]
        public IActionResult CancelJob(string jobId)
        {
            _clusterService.CancelRemoteJob(jobId);
            return Ok(new { cancelled = true });
        }

        [HttpPost("jobs/{jobId}/progress")]
        public async Task<IActionResult> ReportProgress(string jobId, [FromBody] JobProgress progress)
        {
            progress.JobId = jobId;
            await _clusterService.HandleRemoteProgressAsync(jobId, progress);
            return Ok();
        }

        [HttpPost("jobs/{jobId}/complete")]
        public IActionResult ReportCompletion(string jobId)
        {
            // Construct the node URL from the registered node, not from untrusted input (prevents SSRF)
            var nodes = _clusterService.GetNodes();
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var node = nodes.FirstOrDefault(n =>
                n.IpAddress == remoteIp || n.IpAddress == HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString());

            if (node == null)
                return Unauthorized(new { error = "Unknown node" });

            // Return immediately — download the result in the background so the node's POST doesn't time out
            var nodeBaseUrl = $"http://{node.IpAddress}:{node.Port}";
            _ = Task.Run(() => _clusterService.HandleRemoteCompletionAsync(jobId, nodeBaseUrl));
            return Ok();
        }

        [HttpPost("jobs/{jobId}/failed")]
        public async Task<IActionResult> ReportFailure(string jobId, [FromBody] JsonElement body)
        {
            string? errorMessage = null;
            if (body.TryGetProperty("errorMessage", out var msgProp))
                errorMessage = msgProp.GetString();

            await _clusterService.HandleRemoteFailureAsync(jobId, errorMessage);
            return Ok();
        }

        // --- File transfer ---

        [HttpPut("files/{jobId}")]
        [RequestSizeLimit(75_000_000)] // 75MB — chunks are 50MB with headroom
        public async Task<IActionResult> ReceiveFile(string jobId)
        {
            try
            {
                // Mark this node as busy receiving so heartbeat reports it correctly
                _clusterService.SetReceivingJob(jobId);

                var rawFileName = Request.Headers["X-Original-FileName"].FirstOrDefault() ?? "input.mkv";
                var fileName = Path.GetFileName(rawFileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "input.mkv";
                var tempDir = _clusterService.GetNodeTempDirectory(jobId);
                var filePath = Path.Combine(tempDir, fileName);

                // Support resume: if Range header present, append to existing partial file
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

                // Stream directly to disk while computing hash incrementally — avoids buffering the entire chunk in memory
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

                // Broadcast download progress on this node's UI
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
        /// Check how much of a file has been received (for resume support).
        /// </summary>
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
        /// Serves a chunk of the output file. Master calls this repeatedly with Range headers.
        /// </summary>
        [HttpGet("files/{jobId}/output")]
        public async Task<IActionResult> DownloadOutput(string jobId)
        {
            var outputPath = _clusterService.GetOutputFileForJob(jobId);
            if (outputPath == null || !System.IO.File.Exists(outputPath))
                return NotFound(new { error = "Output file not found" });

            var totalSize = new FileInfo(outputPath).Length;

            // Parse range for chunked download
            long offset = 0;
            var rangeHeader = Request.Headers["Range"].FirstOrDefault();
            if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
            {
                var rangeStart = rangeHeader.Replace("bytes=", "").Split('-')[0];
                if (long.TryParse(rangeStart, out var parsed) && parsed > 0 && parsed < totalSize)
                    offset = parsed;
            }

            long chunkSize = 50L * 1024 * 1024; // 50MB
            var length = (int)Math.Min(chunkSize, totalSize - offset);

            var buffer = new byte[length];
            using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var bytesRead = 0;
                while (bytesRead < length)
                {
                    var read = await fs.ReadAsync(buffer.AsMemory(bytesRead, length - bytesRead));
                    if (read == 0) break;
                    bytesRead += read;
                }
                length = bytesRead;
            }

            var chunkHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, length))).ToLower();

            // Update node UI with upload progress
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

            return File(buffer.AsMemory(0, length).ToArray(), "application/octet-stream");
        }

        [HttpGet("files/{jobId}")]
        public IActionResult DownloadSource(string jobId)
        {
            // Master serves the source file for the given job
            // The TranscodingService has the work item with the file path
            var workItem = _clusterService.GetNodes(); // placeholder
            return NotFound(new { error = "Source file serving not implemented via this endpoint" });
        }

        [HttpDelete("files/{jobId}")]
        public async Task<IActionResult> CleanupFiles(string jobId)
        {
            _clusterService.CleanupJobFiles(jobId);

            // Tell the node's UI this job is done
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

        // --- Utility ---

        private long GetAvailableDiskSpace()
        {
            try
            {
                var config = _clusterService.GetConfig();
                var dir = config.NodeTempDirectory ?? Path.Combine(
                    Environment.GetEnvironmentVariable("SNACKS_WORK_DIR") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work"));
                var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
                return drive.AvailableFreeSpace;
            }
            catch { return 0; }
        }
    }
}
