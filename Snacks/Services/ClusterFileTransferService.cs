namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///     Handles chunked file transfers (upload and download) between cluster nodes
///     with SHA256 verification, resume support, and SignalR progress broadcasting.
/// </summary>
public sealed class ClusterFileTransferService
{
    /******************************************************************
     *  Constants
     ******************************************************************/

    /// <summary> Chunk size for file transfers (50 MB). </summary>
    internal const int ChunkSize = 50 * 1024 * 1024;

    /******************************************************************
     *  Dependencies
     ******************************************************************/

    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly IHttpClientFactory          _httpClientFactory;
    private readonly JsonSerializerOptions       _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary> Creates a new <see cref="ClusterFileTransferService"/> with the required dependencies. </summary>
    /// <param name="hubContext"> SignalR hub context for broadcasting progress updates to connected clients. </param>
    /// <param name="httpClientFactory"> Factory for creating <see cref="HttpClient"/> instances. </param>
    public ClusterFileTransferService(
        IHubContext<TranscodingHub> hubContext,
        IHttpClientFactory httpClientFactory)
    {
        _hubContext        = hubContext;
        _httpClientFactory = httpClientFactory;
    }

    /******************************************************************
     *  Public API — Upload
     ******************************************************************/

    /// <summary>
    ///     Registers job metadata on the worker node before uploading begins.
    ///     Called once before the first chunk so the worker knows what encoding
    ///     options to apply once the upload completes.
    /// </summary>
    public async Task RegisterMetadataAsync(HttpClient client, string baseUrl, JobMetadata metadata, CancellationToken ct = default)
    {
        var content = new StringContent(JsonSerializer.Serialize(metadata, _jsonOptions), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{baseUrl}/api/cluster/files/{metadata.JobId}/metadata", content, ct);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"Cluster: Registered metadata for job {metadata.JobId}");
    }

    /// <summary>
    ///     Uploads a source file to a worker node in 50 MB chunks with SHA256 verification.
    ///     Supports resume from the last chunk-aligned byte offset reported by the node.
    ///     Pure binary upload — metadata is registered separately via <see cref="RegisterMetadataAsync"/>.
    /// </summary>
    public async Task UploadFileToNodeAsync(
        HttpClient client, string baseUrl, WorkItem workItem, CancellationToken ct = default)
    {
        var totalSize = workItem.Size;
        Console.WriteLine(
            $"Cluster: Uploading {workItem.FileName} ({totalSize / 1048576}MB) to " +
            $"{workItem.AssignedNodeName} in {ChunkSize / 1048576}MB chunks...");

        long rawOffset = await GetNodeReceivedBytesAsync(client, baseUrl, workItem.Id);
        long offset    = (rawOffset / ChunkSize) * ChunkSize;

        if (rawOffset >= totalSize)
        {
            Console.WriteLine("Cluster: Node already has the complete file — sending completion signal");

            // The node has the file but encoding may not have started (e.g., master
            // crashed after the previous upload). Re-send the last chunk as a zero-offset
            // PUT with the correct headers so ReceiveFile triggers autonomous encoding.
            var signal = new ByteArrayContent(Array.Empty<byte>());
            signal.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/cluster/files/{workItem.Id}");
            request.Content = signal;
            request.Headers.Add("X-Original-FileName", workItem.FileName);
            request.Headers.Add("X-Total-Size",        totalSize.ToString());
            request.Headers.Add("X-Bitrate",           workItem.Bitrate.ToString());
            request.Headers.Add("X-Duration",          workItem.Length.ToString());
            request.Headers.Add("Range",               $"bytes={totalSize}-");

            try
            {
                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    Console.WriteLine($"Cluster: Completion signal returned {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Completion signal failed: {ex.Message}");
            }

            return;
        }

        if (offset > 0)
            Console.WriteLine(
                $"Cluster: Resuming upload at {offset / 1048576}MB (aligned from {rawOffset / 1048576}MB)");

        int       consecutiveFailures    = 0;
        const int MaxConsecutiveFailures = 60;

        using var fileStream = new FileStream(
            workItem.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fileStream.Seek(offset, SeekOrigin.Begin);

        while (offset < totalSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunkLength = (int)Math.Min(ChunkSize, totalSize - offset);
            var chunkBuffer = new byte[chunkLength];
            var bytesRead   = 0;

            while (bytesRead < chunkLength)
            {
                var read = await fileStream.ReadAsync(
                    chunkBuffer.AsMemory(bytesRead, chunkLength - bytesRead), ct);
                if (read == 0) break;
                bytesRead += read;
            }

            var chunkHash = Convert.ToHexString(
                SHA256.HashData(chunkBuffer.AsSpan(0, bytesRead))).ToLower();

            while (true)
            {
                try
                {
                    var chunkContent = new ByteArrayContent(chunkBuffer, 0, bytesRead);
                    chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    var request = new HttpRequestMessage(
                        HttpMethod.Put, $"{baseUrl}/api/cluster/files/{workItem.Id}");
                    request.Content = chunkContent;
                    request.Headers.Add("X-Original-FileName", workItem.FileName);
                    request.Headers.Add("X-Total-Size",        totalSize.ToString());
                    request.Headers.Add("X-Bitrate",           workItem.Bitrate.ToString());
                    request.Headers.Add("X-Duration",          workItem.Length.ToString());
                    request.Headers.Add("Range",               $"bytes={offset}-");
                    request.Headers.Add("X-Chunk-Hash",        chunkHash);

                    var response = await client.SendAsync(request, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync(ct);
                        var statusCode = (int)response.StatusCode;

                        // 409 = offset mismatch — node's file is shorter than our offset.
                        // Re-align by seeking back to the node's actual position.
                        if (statusCode == 409)
                        {
                            var realOffset = await GetNodeReceivedBytesAsync(client, baseUrl, workItem.Id);
                            var aligned = (realOffset / ChunkSize) * ChunkSize;
                            Console.WriteLine(
                                $"Cluster: Offset mismatch at {offset / 1048576}MB — " +
                                $"node has {realOffset / 1048576}MB, re-aligning to {aligned / 1048576}MB");
                            offset = aligned;
                            fileStream.Seek(offset, SeekOrigin.Begin);
                            consecutiveFailures = 0;
                            break; // break inner retry, re-read chunk from new offset in outer loop
                        }

                        // 503 = node rejected encoding after upload completed (paused, busy, etc.)
                        // 400 = bad request — don't retry, it won't fix itself
                        if (statusCode is 503 or 400)
                            throw new InvalidOperationException($"HTTP {statusCode}: {errorBody}");

                        throw new Exception($"HTTP {statusCode}: {errorBody}");
                    }

                    if (response.Headers.TryGetValues("X-Hash-Match", out var hashMatch) &&
                        hashMatch.FirstOrDefault() == "false")
                    {
                        throw new Exception("Chunk hash mismatch — data corrupted in transit");
                    }

                    // Check if the node detected file corruption after this chunk
                    if (response.Headers.TryGetValues("X-Header-Corrupt", out var corruptFlag) &&
                        corruptFlag.FirstOrDefault() == "true")
                    {
                        var fileHeader = response.Headers.TryGetValues("X-File-Header", out var hdrVals)
                            ? hdrVals.FirstOrDefault() : "unknown";
                        throw new InvalidOperationException(
                            $"File header corrupted after writing chunk at offset {offset}. " +
                            $"File header bytes: 0x{fileHeader}. " +
                            $"This chunk was {bytesRead} bytes. " +
                            $"The node's file has been damaged during transfer.");
                    }

                    offset              += bytesRead;
                    consecutiveFailures  = 0;

                    workItem.TransferProgress = (int)(offset * 100 / totalSize);
                    workItem.Status           = WorkItemStatus.Processing;
                    workItem.RemoteJobPhase   = "Uploading";
                    workItem.ErrorMessage     = null;
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem, ct);

                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException)
                {
                    // Non-retryable error (node rejected the request) — fail immediately
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    var delay = 5;

                    Console.WriteLine(
                        $"Cluster: Upload chunk failed at {offset / 1048576}MB " +
                        $"(failure {consecutiveFailures}/{MaxConsecutiveFailures}): " +
                        $"{ex.Message} — retrying in {delay}s...");

                    workItem.ErrorMessage = $"Upload retry {consecutiveFailures} — {ex.Message}";
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem, ct);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                        throw new Exception(
                            $"Upload failed at {offset / 1048576}MB after " +
                            $"{MaxConsecutiveFailures} consecutive failures");

                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
        }

        var secret = client.DefaultRequestHeaders.TryGetValues("X-Snacks-Secret", out var secretValues)
            ? secretValues.FirstOrDefault() ?? ""
            : "";
        var finalSize = await GetNodeReceivedBytesAsync(
            CreateAuthenticatedClient(secret),
            baseUrl, workItem.Id);

        if (finalSize != totalSize)
            throw new Exception(
                $"Upload size mismatch — sent {totalSize}, node has {finalSize}");

        Console.WriteLine(
            $"Cluster: Upload of {workItem.FileName} complete ({totalSize / 1048576}MB)");
    }

    /******************************************************************
     *  Public API — Download
     ******************************************************************/

    /// <summary>
    ///     Downloads an encoded output file from a worker node in 50 MB chunks with SHA256 verification.
    ///     Supports resume from the last chunk-aligned byte offset of the partial file on disk.
    /// </summary>
    /// <param name="client"> An already-authenticated <see cref="HttpClient"/>. </param>
    /// <param name="nodeBaseUrl"> The base URL of the source worker node. </param>
    /// <param name="jobId"> The unique job identifier used to locate the output on the node. </param>
    /// <param name="outputPath"> Local path where the downloaded file will be written. </param>
    /// <param name="workItem"> The work item being downloaded (used for progress updates). </param>
    /// <param name="ct"> Cancellation token. </param>
    public async Task DownloadFileFromNodeAsync(
        HttpClient client, string nodeBaseUrl, string jobId, string outputPath,
        WorkItem workItem, CancellationToken ct = default)
    {
        long   offset                    = 0;
        long   totalSize                 = 0;
        int    consecutiveFailures       = 0;
        const int MaxConsecutiveFailures = 120;

        // If a partial download exists, resume from the last chunk boundary.
        // Round down to discard any partially-written chunk from a killed process —
        // the last partial chunk may be corrupt (same approach as upload resume).
        if (File.Exists(outputPath))
        {
            long rawOffset = new FileInfo(outputPath).Length;
            offset = (rawOffset / ChunkSize) * ChunkSize;
            if (offset > 0)
            {
                using (var truncStream = new FileStream(
                    outputPath, FileMode.Open, FileAccess.Write))
                    truncStream.SetLength(offset);

                Console.WriteLine(
                    $"Cluster: Resuming download at {offset / 1048576}MB " +
                    $"(aligned from {rawOffset / 1048576}MB)");
            }
        }

        while (true)
        {
            try
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{nodeBaseUrl}/api/cluster/files/{jobId}/output");
                if (offset > 0)
                    request.Headers.Add("Range", $"bytes={offset}-");

                var response = await client.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    throw new Exception($"HTTP {(int)response.StatusCode}: {errorBody}");
                }

                // Get total size from header
                if (response.Headers.TryGetValues("X-Total-Size", out var sizeValues) &&
                    long.TryParse(sizeValues.FirstOrDefault(), out var ts))
                    totalSize = ts;

                // Get chunk hash for verification
                string? expectedHash = null;
                if (response.Headers.TryGetValues("X-Chunk-Hash", out var hashValues))
                    expectedHash = hashValues.FirstOrDefault();

                var chunkData = await response.Content.ReadAsByteArrayAsync(ct);
                if (chunkData.Length == 0)
                {
                    if (totalSize > 0 && offset < totalSize)
                        throw new Exception("Empty chunk received before download complete");
                    break;
                }

                // Verify chunk hash
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var actualHash = Convert.ToHexString(
                        SHA256.HashData(chunkData)).ToLower();
                    if (actualHash != expectedHash)
                        throw new Exception("Chunk hash mismatch during download");
                }

                // Write chunk to disk
                var mode = offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;
                using (var fs = new FileStream(outputPath, mode, FileAccess.Write))
                {
                    if (offset > 0)
                        fs.Seek(offset, SeekOrigin.Begin);
                    await fs.WriteAsync(chunkData, ct);
                }

                offset              += chunkData.Length;
                consecutiveFailures  = 0;

                // Update progress
                if (totalSize > 0)
                {
                    workItem.TransferProgress = (int)(offset * 100 / totalSize);
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem, ct);
                }

                // Check if we've downloaded everything
                if (totalSize > 0 && offset >= totalSize) break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = Math.Min(consecutiveFailures * 10, 60);

                Console.WriteLine(
                    $"Cluster: Download chunk failed at {offset / 1048576}MB " +
                    $"(failure {consecutiveFailures}/{MaxConsecutiveFailures}): " +
                    $"{ex.Message} — retrying in {delay}s...");

                workItem.ErrorMessage = $"Download retry {consecutiveFailures} — {ex.Message}";
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem, ct);

                if (consecutiveFailures >= MaxConsecutiveFailures)
                    throw new Exception(
                        $"Download failed after {MaxConsecutiveFailures} consecutive failures " +
                        $"at {offset / 1048576}MB");

                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                // Re-check if partial file still exists (could have been cleaned up).
                // Align to chunk boundary to discard any partially-written chunk.
                if (File.Exists(outputPath))
                    offset = (new FileInfo(outputPath).Length / ChunkSize) * ChunkSize;
            }
        }

        // Per-chunk SHA256 verification during download guarantees integrity,
        // so no full-file hash check is needed here.
        workItem.ErrorMessage = null;
        Console.WriteLine($"Cluster: Download of result complete ({offset / 1048576}MB)");
    }

    /******************************************************************
     *  Public API — Queries
     ******************************************************************/

    /// <summary>
    ///     Checks how many bytes of a file have been received by a node via a HEAD request.
    ///     Used for resume support and upload verification.
    /// </summary>
    /// <param name="client"> An already-authenticated <see cref="HttpClient"/>. </param>
    /// <param name="baseUrl"> The base URL of the target worker node. </param>
    /// <param name="jobId"> The unique job identifier. </param>
    /// <returns> The number of bytes the node reports having received, or <c>0</c> on failure. </returns>
    public async Task<long> GetNodeReceivedBytesAsync(
        HttpClient client, string baseUrl, string jobId)
    {
        try
        {
            var headRequest = new HttpRequestMessage(
                HttpMethod.Head, $"{baseUrl}/api/cluster/files/{jobId}");
            var response = await client.SendAsync(headRequest);

            if (response.IsSuccessStatusCode &&
                response.Headers.TryGetValues("X-Received-Bytes", out var values) &&
                long.TryParse(values.FirstOrDefault(), out var received))
            {
                return received;
            }
        }
        catch { }

        return 0;
    }

    /******************************************************************
     *  Public API — Client
     ******************************************************************/

    /// <summary>
    ///     Creates an <see cref="HttpClient"/> pre-configured with the cluster shared secret header
    ///     and a generous timeout suitable for large file transfers over slow networks.
    /// </summary>
    /// <param name="sharedSecret"> The cluster shared secret used for authentication. </param>
    /// <returns> A configured <see cref="HttpClient"/> ready for cluster API calls. </returns>
    public HttpClient CreateAuthenticatedClient(string sharedSecret)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.Add("X-Snacks-Secret", sharedSecret);
        return client;
    }
}

/// <summary>
///     A read-only stream wrapper that reports upload progress as data is read.
///     Tracks bytes read and invokes a callback at most once per second when the
///     completion percentage changes.
/// </summary>
internal sealed class ProgressStream : System.IO.Stream
{
    private readonly System.IO.Stream _inner;
    private readonly long             _totalLength;
    private readonly Func<int, Task>  _onProgress;
    private long                      _bytesRead;
    private int                       _lastPercent;
    private DateTime                  _lastReport = DateTime.MinValue;

    /// <summary>
    ///     Wraps <paramref name="inner"/> and reports read progress through <paramref name="onProgress"/>.
    /// </summary>
    /// <param name="inner"> The underlying stream to read from. </param>
    /// <param name="totalLength"> Total byte length used to compute percentage. </param>
    /// <param name="onProgress"> Async callback receiving the current completion percentage (0–100). </param>
    /// <param name="initialOffset"> Bytes already transferred before this stream started (for resume support). </param>
    public ProgressStream(
        System.IO.Stream inner, long totalLength, Func<int, Task> onProgress, long initialOffset = 0)
    {
        _inner       = inner;
        _totalLength = totalLength;
        _onProgress  = onProgress;
        _bytesRead   = initialOffset;
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        _bytesRead += read;
        await ReportProgressAsync();
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken);
        _bytesRead += read;
        await ReportProgressAsync();
        return read;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        _bytesRead += read;
        return read;
    }

    /// <summary>
    ///     Fires the progress callback when the completion percentage changes,
    ///     rate-limited to at most once per second.
    /// </summary>
    private async Task ReportProgressAsync()
    {
        if (_totalLength <= 0) return;

        int percent = (int)(_bytesRead * 100 / _totalLength);
        var now     = DateTime.UtcNow;

        if (percent != _lastPercent && (now - _lastReport).TotalSeconds >= 1)
        {
            _lastPercent = percent;
            _lastReport  = now;
            await _onProgress(percent);
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _totalLength;

    /// <inheritdoc/>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
