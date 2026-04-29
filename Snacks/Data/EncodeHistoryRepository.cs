using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data;

/// <summary>
///     Repository for the append-only encode-history ledger.
///     Provides the writer hook used by both the master's local scheduler
///     and the cluster completion path, plus the aggregation queries that
///     power the analytics dashboard.
///
///     <para>All aggregations use SQLite-backed group-by queries instead of
///     loading rows into memory — even after years of operation the ledger
///     stays cheap to query because the index on <c>CompletedAt</c> covers
///     every range scan the dashboard issues.</para>
/// </summary>
public class EncodeHistoryRepository
{
    private readonly IDbContextFactory<SnacksDbContext> _contextFactory;

    /// <summary> Creates a new repository using the specified context factory. </summary>
    public EncodeHistoryRepository(IDbContextFactory<SnacksDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    ///     Persists a single completed encode. Negative
    ///     <see cref="EncodeHistory.BytesSaved"/> values are clamped to zero
    ///     so a bigger output (rare; happens with stricter quality settings)
    ///     doesn't pull the dashboard's running totals into the negatives.
    /// </summary>
    public async Task RecordAsync(EncodeHistory record)
    {
        if (record.BytesSaved < 0) record.BytesSaved = 0;
        if (string.IsNullOrEmpty(record.DeviceId)) record.DeviceId = "unknown";
        if (string.IsNullOrEmpty(record.Outcome))  record.Outcome = "Completed";

        using var context = await _contextFactory.CreateDbContextAsync();
        context.EncodeHistory.Add(record);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // History recording is non-critical — never let an analytics
            // write failure abort the encode pipeline. Log and move on.
            Console.WriteLine($"EncodeHistory: failed to record {record.JobId}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Top-line stats for the dashboard hero strip: lifetime totals
    ///     across every completed encode in the ledger.
    /// </summary>
    public async Task<HistorySummary> GetSummaryAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var stats = await context.EncodeHistory
            .GroupBy(_ => 1)
            .Select(g => new HistorySummary
            {
                TotalEncodes      = g.Count(),
                TotalBytesSaved   = g.Sum(e => e.BytesSaved),
                TotalOriginalBytes = g.Sum(e => e.OriginalSizeBytes),
                TotalEncodeSeconds = g.Sum(e => e.EncodeSeconds),
                TotalContentSeconds = g.Sum(e => e.DurationSeconds),
                FourKEncodes      = g.Count(e => e.Is4K),
                NoSavingsEncodes  = g.Count(e => e.Outcome == "NoSavings"),
            })
            .FirstOrDefaultAsync();

        return stats ?? new HistorySummary();
    }

    /// <summary>
    ///     Daily aggregates for the savings-over-time chart.
    ///     Returns one bucket per UTC day in the requested range, including
    ///     empty days so the chart x-axis stays continuous.
    /// </summary>
    public async Task<List<DailyAggregate>> GetSavingsOverTimeAsync(int days)
    {
        if (days <= 0) days = 30;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days + 1);

        using var context = await _contextFactory.CreateDbContextAsync();
        var rows = await context.EncodeHistory
            .Where(e => e.CompletedAt >= cutoff)
            .GroupBy(e => e.CompletedAt.Date)
            .Select(g => new DailyAggregate
            {
                Day             = g.Key,
                Encodes         = g.Count(),
                BytesSaved      = g.Sum(e => e.BytesSaved),
                OriginalBytes   = g.Sum(e => e.OriginalSizeBytes),
                EncodeSeconds   = g.Sum(e => e.EncodeSeconds),
            })
            .ToListAsync();

        // Backfill empty days so the chart renders a continuous x-axis.
        var byDay = rows.ToDictionary(r => r.Day);
        var filled = new List<DailyAggregate>(days);
        for (int i = 0; i < days; i++)
        {
            var d = cutoff.AddDays(i);
            filled.Add(byDay.TryGetValue(d, out var hit)
                ? hit
                : new DailyAggregate { Day = d });
        }
        return filled;
    }

    /// <summary>
    ///     Per-device totals over the trailing window. Drives the device
    ///     workload stripe — total encode hours, files done, and bytes
    ///     saved per device family.
    /// </summary>
    public async Task<List<DeviceAggregate>> GetDeviceUtilizationAsync(int days)
    {
        if (days <= 0) days = 30;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days + 1);

        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.EncodeHistory
            .Where(e => e.CompletedAt >= cutoff)
            .GroupBy(e => e.DeviceId)
            .Select(g => new DeviceAggregate
            {
                DeviceId       = g.Key,
                Encodes        = g.Count(),
                EncodeSeconds  = g.Sum(e => e.EncodeSeconds),
                BytesSaved     = g.Sum(e => e.BytesSaved),
                ContentSeconds = g.Sum(e => e.DurationSeconds),
            })
            .OrderByDescending(d => d.EncodeSeconds)
            .ToListAsync();
    }

    /// <summary>
    ///     Output codec breakdown over the trailing window — feeds the
    ///     dashboard donut chart.
    /// </summary>
    public async Task<List<CodecAggregate>> GetCodecMixAsync(int days)
    {
        if (days <= 0) days = 30;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days + 1);

        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.EncodeHistory
            .Where(e => e.CompletedAt >= cutoff)
            .GroupBy(e => e.EncodedCodec)
            .Select(g => new CodecAggregate
            {
                Codec      = string.IsNullOrEmpty(g.Key) ? "unknown" : g.Key,
                Encodes    = g.Count(),
                BytesSaved = g.Sum(e => e.BytesSaved),
            })
            .OrderByDescending(c => c.Encodes)
            .ToListAsync();
    }

    /// <summary>
    ///     Per-node throughput over the trailing window. Drives the
    ///     "where did the work get done" leaderboard.
    /// </summary>
    public async Task<List<NodeAggregate>> GetNodeThroughputAsync(int days)
    {
        if (days <= 0) days = 30;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days + 1);

        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.EncodeHistory
            .Where(e => e.CompletedAt >= cutoff)
            .GroupBy(e => new { e.NodeId, e.NodeHostname })
            .Select(g => new NodeAggregate
            {
                NodeId         = g.Key.NodeId,
                Hostname       = g.Key.NodeHostname,
                Encodes        = g.Count(),
                BytesSaved     = g.Sum(e => e.BytesSaved),
                EncodeSeconds  = g.Sum(e => e.EncodeSeconds),
            })
            .OrderByDescending(n => n.Encodes)
            .ToListAsync();
    }

    /// <summary>
    ///     Most recent N encodes for the dashboard's recent-activity table.
    /// </summary>
    public async Task<List<EncodeHistory>> GetRecentAsync(int limit)
    {
        if (limit <= 0) limit = 25;
        if (limit > 200) limit = 200;

        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.EncodeHistory
            .OrderByDescending(e => e.CompletedAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    ///     Top compression wins — encodes ordered by the largest absolute
    ///     bytes saved. Surfaces the most "look how much we shrunk this"
    ///     stories for the leaderboard.
    /// </summary>
    public async Task<List<EncodeHistory>> GetTopSavingsAsync(int limit, int days)
    {
        if (limit <= 0) limit = 10;
        if (limit > 100) limit = 100;
        if (days <= 0) days = 365;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days + 1);

        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.EncodeHistory
            .Where(e => e.CompletedAt >= cutoff && e.Outcome == "Completed")
            .OrderByDescending(e => e.BytesSaved)
            .Take(limit)
            .ToListAsync();
    }
}

/// <summary>Lifetime total stats for the dashboard hero strip.</summary>
public sealed class HistorySummary
{
    public int    TotalEncodes        { get; set; }
    public long   TotalBytesSaved     { get; set; }
    public long   TotalOriginalBytes  { get; set; }
    public double TotalEncodeSeconds  { get; set; }
    public double TotalContentSeconds { get; set; }
    public int    FourKEncodes        { get; set; }
    public int    NoSavingsEncodes    { get; set; }
}

/// <summary>Per-day rollup for the savings time-series chart.</summary>
public sealed class DailyAggregate
{
    public DateTime Day           { get; set; }
    public int      Encodes       { get; set; }
    public long     BytesSaved    { get; set; }
    public long     OriginalBytes { get; set; }
    public double   EncodeSeconds { get; set; }
}

/// <summary>Per-device rollup for the device utilization stripe.</summary>
public sealed class DeviceAggregate
{
    public string DeviceId       { get; set; } = "";
    public int    Encodes        { get; set; }
    public double EncodeSeconds  { get; set; }
    public long   BytesSaved     { get; set; }
    public double ContentSeconds { get; set; }
}

/// <summary>Per-codec rollup for the dashboard donut.</summary>
public sealed class CodecAggregate
{
    public string Codec      { get; set; } = "";
    public int    Encodes    { get; set; }
    public long   BytesSaved { get; set; }
}

/// <summary>Per-node rollup for the throughput leaderboard.</summary>
public sealed class NodeAggregate
{
    public string NodeId        { get; set; } = "";
    public string Hostname      { get; set; } = "";
    public int    Encodes       { get; set; }
    public long   BytesSaved    { get; set; }
    public double EncodeSeconds { get; set; }
}
