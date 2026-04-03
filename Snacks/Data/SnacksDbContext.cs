using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data
{
    public class SnacksDbContext : DbContext
    {
        public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

        public SnacksDbContext(DbContextOptions<SnacksDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MediaFile>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.FilePath).IsUnique();
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => new { e.Directory, e.BaseName });

                entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
                entity.Property(e => e.Directory).IsRequired().HasMaxLength(1024);
                entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
                entity.Property(e => e.BaseName).IsRequired().HasMaxLength(512);
                entity.Property(e => e.Codec).HasMaxLength(32);
                entity.Property(e => e.PixelFormat).HasMaxLength(32);
                entity.Property(e => e.FailureReason).HasMaxLength(2048);

                // Cluster remote job tracking
                entity.Property(e => e.RemoteWorkItemId).HasMaxLength(64);
                entity.Property(e => e.AssignedNodeId).HasMaxLength(64);
                entity.Property(e => e.AssignedNodeName).HasMaxLength(128);
                entity.Property(e => e.RemoteJobPhase).HasMaxLength(32);
                entity.Property(e => e.AssignedNodeIp).HasMaxLength(64);
            });
        }
    }
}
