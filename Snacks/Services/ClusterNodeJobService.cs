namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
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
    private          DateTime                 _lastReceiveActivity;
    private volatile bool                     _nodePaused;
    private readonly object                   _jobStartLock = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim>           _receiveLocks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _receiveCts   = new();

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

    /// <summary> Returns the completed job ID if encoding finished but cleanup hasn't occurred yet. </summary>
    public string? GetCompletedJobId() => _completedJobId;

    /// <summary> Returns the job ID currently being received via file transfer, or null. </summary>
    public string? GetReceivingJobId() => _receivingJobId;

    /// <summary>
    ///     Returns a per-job semaphore that serializes concurrent ReceiveFile requests.
    ///     Prevents file-lock collisions when a retry arrives before the previous request's
    ///     FileStream has been disposed.
    /// </summary>
    public SemaphoreSlim GetReceiveLock(string jobId) =>
        _receiveLocks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    ///     Cancels any in-flight ReceiveFile request for this job and returns a fresh
    ///     <see cref="CancellationToken"/> for the new request. The old handler's
    ///     body-read will throw <see cref="OperationCanceledException"/>, releasing the
    ///     file handle and the per-job semaphore so the retry can proceed.
    /// </summary>
    public CancellationToken SwapReceiveCts(string jobId)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = _receiveCts.AddOrUpdate(jobId, newCts, (_, old) => { old.Cancel(); return newCts; });
        return newCts.Token;
    }

    /// <summary> Removes the receive lock and cancellation source for a completed/cleaned-up job. </summary>
    public void RemoveReceiveLock(string jobId)
    {
        _receiveLocks.TryRemove(jobId, out _);
        if (_receiveCts.TryRemove(jobId, out var cts)) cts.Dispose();
    }

    /// <summary>
    ///     Clears a stale receiving state if no chunks have arrived within the timeout.
    ///     Called periodically from the heartbeat timer.
    /// </summary>
    public void ExpireStaleReceiving(TimeSpan timeout)
    {
        if (_receivingJobId != null && _currentRemoteJob == null &&
            (DateTime.UtcNow - _lastReceiveActivity) > timeout)
        {
            var staleId = _receivingJobId;
            _receivingJobId = null;
            Console.WriteLine($"Cluster: Cleared stale receiving state for job {staleId} (no activity for {timeout.TotalSeconds:0}s)");

            _ = _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
            {
                id             = staleId,
                status         = "Completed",
                progress       = 100,
                remoteJobPhase = (string?)null,
                completedAt    = DateTime.UtcNow
            });
        }
    }

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
        if (jobId != null) _lastReceiveActivity = DateTime.UtcNow;

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
     ///     Starts autonomous encoding after a file upload completes.
     ///     Validates the file exists, verifies its hash, and begins encoding in a background task.
     ///     This replaces the two-phase commit (upload → offer → accept) with autonomous encoding.
     /// </summary>
     /// <param name="jobId">The job ID that was uploaded.</param>
     /// <param name="metadata">The job metadata sent with the upload.</param>
     /// <param name="filePath">The local path where the file was saved.</param>
     /// <returns><see langword="true"/> if encoding was started; <see langword="false"/> otherwise.</returns>
    public async Task<(bool Started, string? RejectReason)> StartAutonomousEncodingAsync(string jobId, JobMetadata metadata, string filePath)
    {
        if (_nodePaused)
            return (false, "Node is paused");

        // Lock the check-and-set to prevent concurrent uploads from racing past
        // the _currentRemoteJob guard — without this, two ReceiveFile completions
        // arriving back-to-back can both see null and start encoding simultaneously,
        // overwhelming the hardware encoder.
        lock (_jobStartLock)
        {
            if (_currentRemoteJob != null)
                return (false, $"Already processing job {_currentRemoteJob.Id} ({_currentRemoteJob.FileName})");

            // Claim the slot immediately so no other caller can race past
            _currentRemoteJob = new WorkItem
            {
                Id       = jobId,
                FileName = metadata.FileName,
                Status   = WorkItemStatus.Processing
            };
        }

        // Validation — if any check fails, release the slot
        if (!File.Exists(filePath))
        {
            _currentRemoteJob = null;
            return (false, $"File not found at {filePath}");
        }

        // Check if we already have the encoded output — skip encoding
        var existingOutput = GetOutputFileForJob(jobId);
        if (existingOutput != null)
        {
            Console.WriteLine($"Cluster: Output already exists for {metadata.FileName} — skipping encode, ready for download");
            _currentRemoteJob = null;
            _completedJobId = jobId;
            _receivingJobId = null;
            return (true, null);
        }

        // Verify uploaded file size matches expected — chunk hashes already
        // guaranteed per-chunk integrity, so a size check is sufficient here.
        var actualSize = new FileInfo(filePath).Length;
        if (actualSize != metadata.FileSize)
        {
            _currentRemoteJob = null;
            return (false, $"File size mismatch — expected {metadata.FileSize}, got {actualSize}");
        }

        // Verify the file isn't corrupt at byte 0 — the most common failure mode
        // from interrupted chunk writes is null bytes at the start of the file.
        try
        {
            var header = new byte[4];
            using var checkStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var headerRead = await checkStream.ReadAsync(header);
            if (headerRead >= 4 && header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x00 && header[3] == 0x00)
            {
                _currentRemoteJob = null;
                return (false, $"File corrupt — first 4 bytes are 0x00000000 (expected container header). " +
                    $"Size on disk: {actualSize}, expected: {metadata.FileSize}");
            }
        }
        catch { }

        // Replace the placeholder with the full work item
        var workItem = new WorkItem
        {
            Id        = jobId,
            FileName  = metadata.FileName,
            Path      = filePath,
            Size      = metadata.FileSize,
            Bitrate   = metadata.Bitrate,
            Length    = metadata.Duration,
            IsHevc    = metadata.IsHevc,
            Probe     = metadata.Probe,
            Status    = WorkItemStatus.Processing,
            StartedAt = DateTime.UtcNow
        };

        _currentRemoteJob = workItem;
        _receivingJobId   = null;
        _remoteJobCts     = new CancellationTokenSource();

        _ = Task.Run(() => ExecuteRemoteJobAsync(workItem, metadata.Options));

        return (true, null);
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

            // Clean up node state BEFORE reporting failure to master — the master
            // reacts immediately by marking the node online and dispatching new work.
            // If we report first and clean up after, the new job's upload can arrive
            // while _currentRemoteJob is still set, or worse, complete and start
            // encoding concurrently with leftover retry processes.
            _completedJobId = null;
            _currentRemoteJob = null;
            _remoteJobCts?.Dispose();
            _remoteJobCts = null;
            _transcodingService.SetProgressCallback(null);
            _transcodingService.SetLogCallback(null);

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

        // Check if encoding succeeded but the output was deleted (no savings case).
        // ConvertAsync deletes the output when the encoded file isn't smaller than the
        // original, so we need to detect this before reporting completion to the master.
        var noSavings = encodingSucceeded && GetOutputFileForJob(workItem.Id) == null;
        if (noSavings)
            Console.WriteLine($"Cluster: Encoding succeeded for {workItem.FileName} but no savings — will notify master to skip download");

        try
        {
            if (encodingSucceeded && !noSavings && _currentRemoteJob != null)
            {
                _currentRemoteJob.Progress = 100;
                _currentRemoteJob.Status   = WorkItemStatus.Downloading;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", _currentRemoteJob);
            }

            _completedJobId = encodingSucceeded && !noSavings ? _currentRemoteJob?.Id : null;
            _currentRemoteJob = null;
            _remoteJobCts?.Dispose();
            _remoteJobCts = null;
            _transcodingService.SetProgressCallback(null);
            _transcodingService.SetLogCallback(null);
        }
        catch { }

        // Report completion OUTSIDE the try/catch — encoding succeeded,
        // so even if this POST fails, the output file still exists on disk
        // and the master can discover it via heartbeat or recovery
        if (encodingSucceeded && masterUrl != null)
        {
            if (!noSavings)
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
                        NoSavings      = noSavings,
                        OutputFileName = workItem.FileName
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { completion, nodeBaseUrl = selfUrl }, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/complete", content);
                    Console.WriteLine($"Cluster: Reported {(noSavings ? "no-savings" : "completion")} for {workItem.FileName} to master");

                    if (!noSavings)
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

            // Clean up temp files immediately for no-savings jobs since there's nothing to download
            if (noSavings)
                CleanupJobFiles(workItem.Id);
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
                var selfUrl = $"{(Config.UseHttps ? "https" : "http")}://{ClusterDiscoveryService.GetLocalIpAddress()}:{_discoveryService.GetListeningPort()}";
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
        RemoveReceiveLock(jobId);

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
    ///     Deletes remote job temp directories from a previous crashed session,
    ///     <b>except</b> those that have pending completions awaiting master acknowledgement.
    ///     Called on node startup to reclaim disk space without losing completed work.
    /// </summary>
    public void CleanupAllRemoteJobs()
    {
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;

        if (!Directory.Exists(baseDir)) return;

        // Load pending completions so we can preserve their output directories
        var pendingJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_pendingCompletionsPath))
        {
            try
            {
                var json = File.ReadAllText(_pendingCompletionsPath);
                var list = JsonSerializer.Deserialize<List<PendingCompletion>>(json, _jsonOptions);
                if (list != null)
                    foreach (var pc in list)
                        pendingJobIds.Add(pc.JobId);
            }
            catch { }
        }

        try
        {
            int cleaned = 0;
            int preserved = 0;
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(dir);
                if (pendingJobIds.Contains(dirName))
                {
                    preserved++;
                    continue;
                }

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
            if (preserved > 0)
                Console.WriteLine($"Cluster: Preserved {preserved} directories with pending completions");
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

}
