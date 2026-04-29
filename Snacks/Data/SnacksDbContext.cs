using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data;

/// <summary>
///     Entity Framework Core database context for the Snacks application.
///     Manages the SQLite connection and schema for media file tracking
///     and distributed job state transitions.
///
///     Uses SQLite with WAL mode for crash resilience and concurrent reads.
/// </summary>
public class SnacksDbContext : DbContext
{
    /// <summary>
    ///     Persistent media file records — the authoritative source of truth
    ///     for file scan results, encoding status, and remote job assignments.
    /// </summary>
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    /// <summary>
    ///     Write-ahead log for distributed job state transitions.
    ///     Records state changes BEFORE they occur, enabling recovery
    ///     of interrupted multi-step operations after crashes.
    /// </summary>
    public DbSet<StateTransition> StateTransitions => Set<StateTransition>();

    /// <summary>
    ///     Append-only ledger of completed encodes. Powers the analytics
    ///     dashboard (savings totals, per-device utilization, codec mix,
    ///     recent-encodes table). Independent from <see cref="MediaFiles"/>;
    ///     a row is written every time a job finishes successfully, even if
    ///     the same file is re-encoded later.
    /// </summary>
    public DbSet<EncodeHistory> EncodeHistory => Set<EncodeHistory>();

    /// <summary> Creates a new context with the specified database options. </summary>
    public SnacksDbContext(DbContextOptions<SnacksDbContext> options) : base(options) { }

    /// <summary> Configures the entity schema, indexes, and constraints. </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        /******************************************************************
         *  MediaFile Configuration
         ******************************************************************/

        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.FilePath).IsUnique();
            entity.HasIndex(e => e.Status);

            // Composite index covers directory-scoped base-name lookups used for format-change detection.
            entity.HasIndex(e => new { e.Directory, e.BaseName });

            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.Directory).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.BaseName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Codec).HasMaxLength(32);
            entity.Property(e => e.PixelFormat).HasMaxLength(32);
            entity.Property(e => e.FailureReason).HasMaxLength(2048);

            entity.Property(e => e.RemoteWorkItemId).HasMaxLength(64);
            entity.Property(e => e.AssignedNodeId).HasMaxLength(64);
            entity.Property(e => e.AssignedNodeName).HasMaxLength(128);
            entity.Property(e => e.RemoteJobPhase).HasMaxLength(32);
            entity.Property(e => e.AssignedNodeIp).HasMaxLength(64);

            // Bounded to prevent pathological track counts from bloating rows. Typical
            // payload is ~30 B/audio track and ~20 B/subtitle track; 4 KB holds ~100 tracks.
            entity.Property(e => e.AudioStreams).HasMaxLength(4096);
            entity.Property(e => e.SubtitleStreams).HasMaxLength(4096);
        });


        /******************************************************************
         *  StateTransition Configuration
         ******************************************************************/

        modelBuilder.Entity<StateTransition>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.WorkItemId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.FromPhase).HasMaxLength(32);
            entity.Property(e => e.ToPhase).IsRequired().HasMaxLength(32);

            // Composite index covers the recovery query that filters by work item and completion flag.
            entity.HasIndex(e => new { e.WorkItemId, e.Completed });
        });


        /******************************************************************
         *  EncodeHistory Configuration
         ******************************************************************/

        modelBuilder.Entity<EncodeHistory>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.JobId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.OriginalCodec).HasMaxLength(32);
            entity.Property(e => e.EncodedCodec).HasMaxLength(32);
            entity.Property(e => e.DeviceId).HasMaxLength(32);
            entity.Property(e => e.NodeId).HasMaxLength(64);
            entity.Property(e => e.NodeHostname).HasMaxLength(128);
            entity.Property(e => e.Outcome).HasMaxLength(16);

            // Time-series queries (savings-over-time, recent encodes) all key off
            // CompletedAt; index it so the dashboard's date-range scans don't
            // table-scan the whole ledger.
            entity.HasIndex(e => e.CompletedAt);
            // Per-device aggregations group on (DeviceId, CompletedAt).
            entity.HasIndex(e => new { e.DeviceId, e.CompletedAt });
        });
    }
}
