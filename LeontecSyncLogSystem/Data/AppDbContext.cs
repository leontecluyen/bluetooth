using Microsoft.EntityFrameworkCore;
using LeontecSyncLogSystem.Models;

namespace LeontecSyncLogSystem.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<LogEntry> Logs => Set<LogEntry>();
        public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();
        public DbSet<CsvUpload> CsvUploads => Set<CsvUpload>();
        public DbSet<MonitorEntry> MonitorEntries => Set<MonitorEntry>();
        public DbSet<PalletOp> PalletOps => Set<PalletOp>();
        public DbSet<PalletOpItem> PalletOpItems => Set<PalletOpItem>();
        public DbSet<DirectEntry> DirectEntries => Set<DirectEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DeviceRecord>(entity =>
            {
                entity.ToTable("Devices");
                entity.HasKey(e => e.Address);
                entity.Property(e => e.Address).HasMaxLength(64);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.WorkerId).HasMaxLength(100);
            });

            modelBuilder.Entity<CsvUpload>(entity =>
            {
                entity.ToTable("CsvUploads");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DeviceAddress).IsRequired().HasMaxLength(64);
                entity.Property(e => e.Source).HasMaxLength(20);
                entity.Property(e => e.Device).HasMaxLength(200);
                entity.Property(e => e.WorkerId).HasMaxLength(100);
                entity.HasIndex(e => e.DeviceAddress);

                entity.Property(e => e.Type).HasMaxLength(20);
                entity.Property(e => e.TermId).HasMaxLength(100);
                entity.HasIndex(e => new { e.TermId, e.Type });
                // Per-day log filter on the dashboard queries by (Type, LogDate).
                entity.HasIndex(e => new { e.Type, e.LogDate });

                // Device (1) → (*) CsvUploads. Deleting a device removes its uploads.
                entity.HasOne<DeviceRecord>()
                    .WithMany()
                    .HasForeignKey(e => e.DeviceAddress)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // CsvUpload (1) → (*) MonitorEntries / PalletOps (cascade delete with the upload).
            modelBuilder.Entity<MonitorEntry>(entity =>
            {
                entity.ToTable("MonitorEntries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PalletOp>(entity =>
            {
                entity.ToTable("PalletOps");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PalletOpItem>(entity =>
            {
                entity.ToTable("PalletOpItems");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.PalletOpId);
                entity.HasOne(i => i.PalletOp).WithMany(o => o.Items).HasForeignKey(i => i.PalletOpId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<DirectEntry>(entity =>
            {
                entity.ToTable("DirectEntries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LogEntry>(entity =>
            {
                entity.ToTable("SyncLogs");

                // LogId is the natural PK and the deduplication key. We do NOT let the
                // database generate it — the device supplies (or we derive) a stable Guid.
                entity.HasKey(e => e.LogId);
                entity.Property(e => e.LogId).ValueGeneratedNever();

                entity.Property(e => e.WorkerId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.JobType).IsRequired().HasMaxLength(100);
                entity.Property(e => e.BarcodeData).IsRequired().HasMaxLength(1024);
                entity.Property(e => e.StartTime).IsRequired();
                entity.Property(e => e.EndTime).IsRequired();
                entity.Property(e => e.SyncMethod).IsRequired().HasMaxLength(20);

                entity.HasIndex(e => e.WorkerId);
                entity.HasIndex(e => e.StartTime);
            });
        }
    }
}
