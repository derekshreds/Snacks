using Snacks.Models;
using System.Collections.Concurrent;

namespace Snacks.Services;

/// <summary>
///     Runs dry-run library analyses as background jobs. The analyze endpoint used to
///     execute the whole walk inside a single HTTP request, which timed out (browser,
///     proxy, or Kestrel — whichever gave up first) on libraries with tens of
///     thousands of files. A job survives the request: the UI starts it, polls
///     progress, and fetches the result set when the job completes.
/// </summary>
public sealed class LibraryAnalysisJobService
{
    /// <summary> Lifecycle states surfaced to the polling UI. </summary>
    public static class JobState
    {
        public const string Running   = "running";
        public const string Completed = "completed";
        public const string Failed    = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary> One analysis run. Progress fields are written by worker threads and read by the poll endpoint. </summary>
    public sealed class AnalysisJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string DirectoryPath { get; init; } = "";
        public bool Recursive { get; init; }
        public string State { get; set; } = JobState.Running;

        /// <summary> Total file count; -1 until directory enumeration finishes. </summary>
        public int Total { get; set; } = -1;
        public int Processed { get; set; }
        public string? Error { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }

        /// <summary> Populated when the job completes. Fetched once by the UI, then pruned with the job. </summary>
        public List<FileAnalysisResult> Results { get; set; } = new();

        internal CancellationTokenSource Cts { get; } = new();
    }

    private readonly TranscodingService _transcodingService;
    private readonly ConcurrentDictionary<string, AnalysisJob> _jobs = new();

    /// <summary> Serializes the check-then-insert in <see cref="Start"/> so two concurrent requests can't both pass the single-job rule. </summary>
    private readonly object _startLock = new();

    /// <summary> How long finished jobs (and their result lists) stay fetchable before pruning. </summary>
    private static readonly TimeSpan FinishedJobRetention = TimeSpan.FromMinutes(30);

    public LibraryAnalysisJobService(TranscodingService transcodingService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        _transcodingService = transcodingService;
    }

    /// <summary>
    ///     Starts a new analysis job and returns immediately. Only one job may run at
    ///     a time — analyses are probe-heavy and a second concurrent walk would double
    ///     the I/O pressure the single-job limit exists to bound.
    /// </summary>
    /// <exception cref="InvalidOperationException"> A job is already running. </exception>
    public AnalysisJob Start(string directoryPath, EncoderOptions options, bool recursive)
    {
        AnalysisJob job;
        lock (_startLock)
        {
            PruneFinishedJobs();
            if (_jobs.Values.Any(j => j.State == JobState.Running))
                throw new InvalidOperationException("An analysis is already running — cancel it or wait for it to finish.");

            job = new AnalysisJob { DirectoryPath = directoryPath, Recursive = recursive };
            _jobs[job.Id] = job;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await _transcodingService.AnalyzeDirectoryAsync(
                    directoryPath, options, recursive, job.Cts.Token,
                    progress: (processed, total) =>
                    {
                        // Reports arrive from parallel workers slightly out of order;
                        // never let the visible counter move backwards.
                        if (processed > job.Processed) job.Processed = processed;
                        job.Total = total;
                    });
                job.Results = results;
                job.State   = JobState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Error = ex.Message;
                Console.WriteLine($"Library analysis job {job.Id} failed: {ex}");
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
                job.Cts.Dispose();

                // Enforce retention even when no further job is ever started —
                // a whole-library result list is tens of MB, and leaving it
                // pinned in this singleton would quietly undo the memory work
                // the rest of the queue does.
                _ = Task.Delay(FinishedJobRetention).ContinueWith(t => _jobs.TryRemove(job.Id, out var removed));
            }
        });
        return job;
    }

    /// <summary> Returns the job, or null when unknown / already pruned. </summary>
    public AnalysisJob? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    /// <summary> Requests cancellation. True when the job existed and was still running. </summary>
    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Running) return false;
        try { job.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary> Drops finished jobs past retention so result lists don't pile up in memory. </summary>
    private void PruneFinishedJobs()
    {
        var cutoff = DateTime.UtcNow - FinishedJobRetention;
        foreach (var job in _jobs.Values)
        {
            if (job.State != JobState.Running && job.FinishedAt is { } finished && finished < cutoff)
                _jobs.TryRemove(job.Id, out _);
        }
    }
}
