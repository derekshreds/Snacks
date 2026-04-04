namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///     Encapsulates all node-side job execution logic for the distributed encoding cluster.
///     Handles receiving, encoding, completion tracking, retry, and cleanup of remote jobs
///     dispatched by the master node.
///
///     <para>This service is <b>not</b> an <see cref="IHostedService"/>; it is owned and
///     driven by <see cref="ClusterService"/>.</para>
/// </summary>
public sealed class ClusterNodeJobService
{
    /******************************************************************
     *  Dependencies
     ******************************************************************/

    private readonly TranscodingService                        _transcodingService;
    private readonly IHubContext<TranscodingHub>                _hubContext;
    private readonly IHttpClientFactory                        _httpClientFactory;
    private readonly ClusterDiscoveryService                   _discoveryService;
    private readonly ConcurrentDictionary<string, ClusterNode> _nodes;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /******************************************************************
     *  Cross-Thread State
     ******************************************************************/

    private volatile WorkItem?                _currentRemoteJob;
    private volatile CancellationTokenSource? _remoteJobCts;
    private volatile string?                  _completedJobId;
    private volatile string?                  _receivingJobId;
    private volatile bool                     _nodePaused;

    /******************************************************************
     *  Pending Completions Persistence
     ******************************************************************/

    private readonly string        _pendingCompletionsPath;
    private readonly SemaphoreSlim _pendingCompletionsLock = new(1, 1);

    /******************************************************************
     *  Configuration
     ******************************************************************/

    /// <summary>
    ///     The current cluster configuration. Set by <see cref="ClusterService"/> whenever
    ///     the configuration is loaded or changed.
    /// </summary>
    public ClusterConfig Config { get; set; } = new();

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Creates a new node-side job service with all required dependencies.
    /// </summary>
    /// <param name="transcodingService">Performs the actual video encoding.</param>
    /// <param name="hubContext">SignalR hub used to broadcast UI updates.</param>
    /// <param name="httpClientFactory">Factory for HTTP clients used to communicate with the master.</param>
    /// <param name="discoveryService">Provides local IP address and port resolution.</param>
    /// <param name="nodes">Shared node registry for locating the master when no explicit URL is configured.</param>
    public ClusterNodeJobService(
        TranscodingService                        transcodingService,
        IHubContext<TranscodingHub>                hubContext,
        IHttpClientFactory                        httpClientFactory,
        ClusterDiscoveryService                   discoveryService,
        ConcurrentDictionary<string, ClusterNode> nodes)
    {
        _transcodingService = transcodingService;
        _hubContext          = hubContext;
        _httpClientFactory   = httpClientFactory;
        _discoveryService    = discoveryService;
        _nodes               = nodes;

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        _pendingCompletionsPath = Path.Combine(workDir, "config", "pending-completions.json");
    }

    /******************************************************************
     *  Node Pause
     ******************************************************************/

    /// <summary> Whether this node is paused and not accepting new jobs. </summary>
    public bool IsNodePaused => _nodePaused;

    /// <summary>
    ///     Pauses or resumes this node's ability to accept new jobs and broadcasts the
    ///     state change to all connected SignalR clients.
    /// </summary>
    /// <param name="paused">When <see langword="true"/>, the node will not accept new job offers.</param>
    public void SetNodePaused(bool paused)
    {
        _nodePaused = paused;
        Console.WriteLine($"Cluster: Node {(paused ? "paused" : "resumed")}");
        _ = _hubContext.Clients.All.SendAsync("ClusterNodePaused", paused);
    }

    /******************************************************************
     *  Job Status Queries
     ******************************************************************/

    /// <summary>
    ///     Returns <see langword="true"/> when this node has any active, receiving, or
    ///     recently-completed remote job tracked in memory.
    /// </summary>
    public bool IsProcessingRemoteJob() =>
        _currentRemoteJob != null || _receivingJobId != null || _completedJobId != null;

    /// <summary>
    ///     Returns the current remote job ID spanning the active, receiving, and completed
    ///     states, or <see langword="null"/> if no remote job is tracked.
    /// </summary>
    /// <returns>The job ID, or <see langword="null"/>.</returns>
    public string? GetCurrentRemoteJobId() =>
        _currentRemoteJob?.Id ?? _receivingJobId ?? _completedJobId;

    /// <summary> Returns the encoding progress percentage of the current remote job. </summary>
    /// <returns>A value from 0 to 100, or 0 if no job is active.</returns>
    public int GetCurrentRemoteJobProgress() => _currentRemoteJob?.Progress ?? 0;

    /// <summary>
    ///     Tracks which job ID is currently being received via file transfer. Used to
    ///     report accurate node status during the upload phase. When a new job ID replaces
    ///     an existing one (e.g. master restarted), the old item is marked completed in the UI.
    /// </summary>
    /// <param name="jobId">The job ID being received, or <see langword="null"/> to clear.</param>
    public void SetReceivingJob(string? jobId)
    {
        var oldJobId = _receivingJobId;
        _receivingJobId = jobId;

        if (oldJobId != null && oldJobId != jobId)
        {
            _ = _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
            {
                id             = oldJobId,
                status         = "Completed",
                progress       = 100,
                remoteJobPhase = (string?)null,
                completedAt    = DateTime.UtcNow
            });
        }
    }

    /******************************************************************
     *  Job Acceptance
     ******************************************************************/

    /// <summary>
    ///     Accepts or rejects a job offer from the master. Validates that the source file
    ///     exists on disk, its size matches the assignment, and its SHA-256 hash is correct.
    ///     If the encoded output already exists, the job is accepted immediately without
    ///     re-encoding. Otherwise, encoding begins in a background task.
    /// </summary>
    /// <param name="assignment">The job assignment sent by the master node.</param>
    /// <returns><see langword="true"/> if the job was accepted; <see langword="false"/> otherwise.</returns>
    public async Task<bool> AcceptJobOfferAsync(JobAssignment assignment)
    {
        if (_nodePaused)
            return false;

        if (_currentRemoteJob != null)
            return false;

        var tempDir   = GetNodeTempDirectory(assignment.JobId);
        var inputPath = Path.Combine(tempDir, assignment.FileName);

        if (!File.Exists(inputPath))
            return false;

        // Check if we already have the encoded output — skip encoding
        var existingOutput = GetOutputFileForJob(assignment.JobId);
        if (existingOutput != null)
        {
            Console.WriteLine($"Cluster: Output already exists for {assignment.FileName} — skipping encode, ready for download");
            _completedJobId = assignment.JobId;
            _receivingJobId = null;
            return true;
        }

        // Verify uploaded file size matches expected
        var actualSize = new FileInfo(inputPath).Length;
        if (actualSize != assignment.FileSize)
        {
            Console.WriteLine($"Cluster: File size mismatch for {assignment.FileName} — expected {assignment.FileSize}, got {actualSize}. Rejecting job.");
            try { File.Delete(inputPath); } catch { }
            return false;
        }

        // Verify source file hash for end-to-end integrity
        if (!string.IsNullOrEmpty(assignment.SourceFileHash))
        {
            var actualHash = await ComputeFileHashAsync(inputPath);
            if (!string.Equals(actualHash, assignment.SourceFileHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Cluster: Source file hash mismatch for {assignment.FileName} — expected {assignment.SourceFileHash}, got {actualHash}. Rejecting job.");
                try { File.Delete(inputPath); } catch { }
                return false;
            }
            Console.WriteLine($"Cluster: Source file hash verified for {assignment.FileName}");
        }

        var workItem = new WorkItem
        {
            Id        = assignment.JobId,
            FileName  = assignment.FileName,
            Path      = inputPath,
            Size      = assignment.FileSize,
            Bitrate   = assignment.Bitrate,
            Length    = assignment.Duration,
            IsHevc    = assignment.IsHevc,
            Probe     = assignment.Probe,
            Status    = WorkItemStatus.Processing,
            StartedAt = DateTime.UtcNow
        };

        _currentRemoteJob = workItem;
        _receivingJobId   = null;
        _remoteJobCts     = new CancellationTokenSource();

        _ = Task.Run(() => ExecuteRemoteJobAsync(workItem, assignment.Options));

        return true;
    }

    /******************************************************************
     *  Job Execution
     ******************************************************************/

    /// <summary>
    ///     Runs the full encoding pipeline for a remote job: configures log and progress
    ///     callbacks, invokes the transcoding service, and reports completion (or failure)
    ///     to the master. On success the completed job is persisted so it can be retried
    ///     on subsequent heartbeats until the master acknowledges receipt.
    /// </summary>
    /// <param name="workItem">The work item describing the file to encode.</param>
    /// <param name="options">Encoder options dictating codec, quality, and hardware settings.</param>
    private async Task ExecuteRemoteJobAsync(WorkItem workItem, EncoderOptions options)
    {
        var masterUrl = ResolveMasterUrl();

        var encodingSucceeded = false;
        try
        {
            var tempDir = GetNodeTempDirectory(workItem.Id);
            options.OutputDirectory     = null;
            options.EncodeDirectory     = tempDir;
            options.DeleteOriginalFile  = false;

            // Hook into log reporting to master — buffer and send every 2 seconds
            var logBuffer   = new ConcurrentQueue<string>();
            var lastLogSend = DateTime.MinValue;

            _transcodingService.SetLogCallback(async (id, message) =>
            {
                logBuffer.Enqueue(message);

                var now = DateTime.UtcNow;
                if ((now - lastLogSend).TotalSeconds < 2) return;
                lastLogSend = now;

                var lines = new List<string>();
                while (logBuffer.TryDequeue(out var line)) lines.Add(line);
                if (lines.Count == 0 || masterUrl == null) return;

                try
                {
                    var client         = CreateAuthenticatedClient();
                    var progressReport = new JobProgress
                    {
                        JobId    = id,
                        Progress = _currentRemoteJob?.Progress ?? 0,
                        Phase    = "Encoding",
                        LogLine  = string.Join("\n", lines)
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(progressReport, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                }
                catch { }
            });

            _transcodingService.SetProgressCallback(async (id, progress) =>
            {
                if (masterUrl != null)
                {
                    try
                    {
                        var client         = CreateAuthenticatedClient();
                        var progressReport = new JobProgress
                        {
                            JobId    = id,
                            Progress = progress,
                            Phase    = "Encoding"
                        };
                        var content = new StringContent(
                            JsonSerializer.Serialize(progressReport, _jsonOptions),
                            Encoding.UTF8, "application/json");
                        await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                    }
                    catch { }
                }
            });

            await _transcodingService.ConvertVideoForRemoteAsync(
                workItem, options, _remoteJobCts?.Token ?? CancellationToken.None);
            encodingSucceeded = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Remote job encoding failed: {ex.Message}");
            if (masterUrl != null)
            {
                try
                {
                    var client  = CreateAuthenticatedClient();
                    var failure = new { jobId = workItem.Id, errorMessage = ex.Message };
                    var content = new StringContent(
                        JsonSerializer.Serialize(failure, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/failed", content);
                }
                catch { }
            }
        }
        finally
        {
            if (encodingSucceeded && _currentRemoteJob != null)
            {
                _currentRemoteJob.Status      = WorkItemStatus.Completed;
                _currentRemoteJob.Progress    = 100;
                _currentRemoteJob.CompletedAt = DateTime.UtcNow;
                _ = _hubContext.Clients.All.SendAsync("WorkItemUpdated", _currentRemoteJob);
            }

            _completedJobId = encodingSucceeded ? _currentRemoteJob?.Id : null;
            _currentRemoteJob = null;
            _remoteJobCts?.Dispose();
            _remoteJobCts = null;
            _transcodingService.SetProgressCallback(null);
            _transcodingService.SetLogCallback(null);
        }

        // Report completion OUTSIDE the try/catch — encoding succeeded,
        // so even if this POST fails, the output file still exists on disk
        // and the master can discover it via heartbeat or recovery
        if (encodingSucceeded && masterUrl != null)
        {
            await PersistCompletedJobAsync(workItem.Id, masterUrl, selfUrl: null, outputFileName: workItem.FileName);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var client  = CreateAuthenticatedClient();
                    var selfUrl = $"{(Config.UseHttps ? "https" : "http")}://{ClusterDiscoveryService.GetLocalIpAddress()}:{_discoveryService.GetListeningPort()}";
                    var completion = new JobCompletion
                    {
                        JobId          = workItem.Id,
                        Success        = true,
                        OutputFileName = workItem.FileName
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { completion, nodeBaseUrl = selfUrl }, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/complete", content);
                    Console.WriteLine($"Cluster: Reported completion for {workItem.FileName} to master");

                    await RemoveCompletedJobAsync(workItem.Id);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cluster: Failed to report completion (attempt {attempt + 1}): {ex.Message}");
                    if (attempt < 9)
                        await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }
    }

    /******************************************************************
     *  Job Cancellation
     ******************************************************************/

    /// <summary>
    ///     Cancels a job running locally on this node. The encoding loop will catch the
    ///     cancellation and clean up.
    /// </summary>
    /// <param name="jobId">The ID of the job to cancel.</param>
    public void CancelRemoteJob(string jobId)
    {
        if (_currentRemoteJob?.Id == jobId)
        {
            Console.WriteLine($"Cluster: Cancelling remote job {jobId}");
            _remoteJobCts?.Cancel();
        }
    }

    /******************************************************************
     *  Pending Completions Persistence
     ******************************************************************/

    /// <summary>
    ///     Appends a completed job record to the pending-completions file so the completion
    ///     can be re-reported on every heartbeat until the master acknowledges it.
    /// </summary>
    /// <param name="jobId">The completed job ID to persist.</param>
    /// <param name="masterUrl">The master's base URL, stored for retry requests.</param>
    /// <param name="selfUrl">This node's base URL, included in completion callbacks.</param>
    public async Task PersistCompletedJobAsync(string jobId, string masterUrl, string? selfUrl, string? outputFileName = null)
    {
        await _pendingCompletionsLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingCompletionsPath)!);
            var completions = await LoadPendingCompletionsInternalAsync();
            if (!completions.ContainsKey(jobId))
            {
                completions[jobId] = new PendingCompletion
                {
                    JobId          = jobId,
                    MasterUrl      = masterUrl,
                    OutputFileName = outputFileName ?? _currentRemoteJob?.FileName ?? "",
                    Timestamp      = DateTime.UtcNow
                };
                var json = JsonSerializer.Serialize(completions.Values, _jsonOptions);
                await File.WriteAllTextAsync(_pendingCompletionsPath, json);
                Console.WriteLine($"Cluster: Persisted completed job {jobId} for retry");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to persist completed job {jobId}: {ex.Message}");
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }
    }

    /// <summary>
    ///     Removes a job from the pending-completions file once the master has acknowledged it.
    /// </summary>
    /// <param name="jobId">The acknowledged job ID to remove.</param>
    public async Task RemoveCompletedJobAsync(string jobId)
    {
        await _pendingCompletionsLock.WaitAsync();
        try
        {
            var completions = await LoadPendingCompletionsInternalAsync();
            if (completions.Remove(jobId))
            {
                var json = JsonSerializer.Serialize(completions.Values, _jsonOptions);
                await File.WriteAllTextAsync(_pendingCompletionsPath, json);
                Console.WriteLine($"Cluster: Removed acknowledged job {jobId} from pending completions");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to remove completed job {jobId}: {ex.Message}");
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }
    }

    /// <summary>
    ///     Loads the pending-completions JSON file, returning an empty dictionary if the
    ///     file does not exist or is corrupt. Must be called under <see cref="_pendingCompletionsLock"/>.
    /// </summary>
    /// <returns>A dictionary keyed by job ID.</returns>
    private async Task<Dictionary<string, PendingCompletion>> LoadPendingCompletionsInternalAsync()
    {
        if (File.Exists(_pendingCompletionsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_pendingCompletionsPath);
                var list = JsonSerializer.Deserialize<List<PendingCompletion>>(json, _jsonOptions);
                return list?.ToDictionary(c => c.JobId, c => c) ?? new();
            }
            catch { }
        }
        return new();
    }

    /// <summary>
    ///     Re-posts all persisted pending completions to the master. Takes a snapshot under
    ///     the lock, then iterates outside the lock to avoid holding it during network I/O.
    ///     Called on each heartbeat cycle to recover from lost completion notifications.
    /// </summary>
    public async Task RetryPendingCompletionsAsync()
    {
        await _pendingCompletionsLock.WaitAsync();
        List<PendingCompletion> snapshot;
        try
        {
            snapshot = (await LoadPendingCompletionsInternalAsync()).Values.ToList();
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }

        foreach (var completion in snapshot)
        {
            try
            {
                var client  = CreateAuthenticatedClient();
                var selfUrl = $"http://{ClusterDiscoveryService.GetLocalIpAddress()}:{_discoveryService.GetListeningPort()}";
                var completionPayload = new JobCompletion
                {
                    JobId          = completion.JobId,
                    Success        = true,
                    OutputFileName = completion.OutputFileName
                };
                var content = new StringContent(
                    JsonSerializer.Serialize(new { completion = completionPayload, nodeBaseUrl = selfUrl }, _jsonOptions),
                    Encoding.UTF8, "application/json");
                var response = await client.PostAsync(
                    $"{completion.MasterUrl}/api/cluster/jobs/{completion.JobId}/complete", content);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Cluster: Retried completion for {completion.JobId} — acknowledged by master");
                    await RemoveCompletedJobAsync(completion.JobId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Pending completion retry failed for {completion.JobId}: {ex.Message}");
            }
        }
    }

    /******************************************************************
     *  Temp Directory and File Management
     ******************************************************************/

    /// <summary>
    ///     Returns the temp directory path for a remote job, creating it if it does not
    ///     exist. The job ID is sanitized to prevent path-traversal attacks.
    /// </summary>
    /// <param name="jobId">The job ID to build the temp directory for.</param>
    /// <returns>The absolute path to the temp directory for this job.</returns>
    public string GetNodeTempDirectory(string jobId)
    {
        var safeJobId = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (string.IsNullOrEmpty(safeJobId)) throw new ArgumentException("Invalid job ID");

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;
        var dir = Path.Combine(baseDir, safeJobId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    ///     Returns the path to the encoded output file for a job, or <see langword="null"/>
    ///     if no output exists yet. Looks for files matching the <c>[snacks]</c> naming
    ///     convention in the job's temp directory.
    /// </summary>
    /// <param name="jobId">The job ID to look up the output for.</param>
    /// <returns>The absolute path to the output file, or <see langword="null"/>.</returns>
    public string? GetOutputFileForJob(string jobId)
    {
        var tempDir = GetNodeTempDirectory(jobId);
        var files   = Directory.GetFiles(tempDir, "*[snacks]*");
        return files.FirstOrDefault();
    }

    /// <summary>
    ///     Deletes all temp files for a completed or cancelled job and clears the
    ///     receiving and completed job ID references if they match.
    /// </summary>
    /// <param name="jobId">The job ID to clean up.</param>
    public void CleanupJobFiles(string jobId)
    {
        if (_receivingJobId == jobId) _receivingJobId = null;
        if (_completedJobId == jobId) _completedJobId = null;

        _transcodingService.RemoveWorkItem(jobId);

        try
        {
            var tempDir = GetNodeTempDirectory(jobId);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Cleanup failed for job {jobId}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deletes all remote job temp directories unconditionally. Called on node startup
    ///     to reclaim disk space from a previous crashed session.
    /// </summary>
    public void CleanupAllRemoteJobs()
    {
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;

        if (!Directory.Exists(baseDir)) return;

        try
        {
            int cleaned = 0;
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                try
                {
                    Directory.Delete(dir, true);
                    cleaned++;
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Cluster: Could not delete {dir}: {ex.Message} — trying individual files");
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            if (cleaned > 0)
                Console.WriteLine($"Cluster: Cleaned up {cleaned} orphaned remote job directories");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deletes remote job temp directories that are older than <paramref name="ttlHours"/>
    ///     hours based on last write time.
    /// </summary>
    /// <param name="ttlHours">Directories last written more than this many hours ago are deleted.</param>
    public void CleanupOldRemoteJobs(int ttlHours)
    {
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;

        if (!Directory.Exists(baseDir)) return;

        var cutoff  = DateTime.UtcNow.AddHours(-ttlHours);
        int cleaned = 0;

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            try
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LastWriteTimeUtc < cutoff)
                {
                    Directory.Delete(dir, true);
                    cleaned++;
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Cluster: Could not delete {dir}: {ex.Message}");
            }
        }

        if (cleaned > 0)
            Console.WriteLine($"Cluster: Cleaned up {cleaned} remote job directories older than {ttlHours}h");
    }

    /******************************************************************
     *  Private Helpers
     ******************************************************************/

    /// <summary>
    ///     Resolves the master node's base URL from the explicit config value or by
    ///     scanning the shared nodes dictionary for a node with the master role.
    /// </summary>
    /// <returns>The master's base URL with no trailing slash, or <see langword="null"/>.</returns>
    private string? ResolveMasterUrl()
    {
        var masterUrl = Config.MasterUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(masterUrl))
            return masterUrl;

        var masterNode = _nodes.Values.FirstOrDefault(n => n.Role == "master");
        return masterNode != null
            ? $"{(Config.UseHttps ? "https" : "http")}://{masterNode.IpAddress}:{masterNode.Port}"
            : null;
    }

    /// <summary>
    ///     Creates an <see cref="HttpClient"/> with the cluster shared secret attached as
    ///     the <c>X-Snacks-Secret</c> header and a generous 30-minute timeout.
    /// </summary>
    /// <returns>A configured <see cref="HttpClient"/>.</returns>
    private HttpClient CreateAuthenticatedClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.Add("X-Snacks-Secret", Config.SharedSecret);
        return client;
    }

    /// <summary>
    ///     Computes the SHA-256 hash of a file for end-to-end source file integrity
    ///     verification after transfer.
    /// </summary>
    /// <param name="filePath">The absolute path to the file to hash.</param>
    /// <returns>The lowercase hex-encoded SHA-256 hash string.</returns>
    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLower();
    }
}
