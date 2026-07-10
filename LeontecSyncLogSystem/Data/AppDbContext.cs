using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using LeontecSyncLogSystem.Models;

namespace LeontecSyncLogSystem.Data
{
    public class AppDbContext : DbContext
    {
        // DateOnly/TimeOnly are backported to net48 by Portable.System.DateTimeOnly, but EF Core 3.1 +
        // Pomelo don't know how to store them. These converters map TimeOnly↔TimeSpan (MySQL TIME) and
        // DateOnly↔DateTime (MySQL DATE) so the DB schema/values match the modern-.NET build.
        private static readonly ValueConverter<TimeOnly, TimeSpan> TimeOnlyConverter =
            new(v => v.ToTimeSpan(), v => TimeOnly.FromTimeSpan(v));
        private static readonly ValueConverter<DateOnly, DateTime> DateOnlyConverter =
            new(v => v.ToDateTime(TimeOnly.MinValue), v => DateOnly.FromDateTime(v));

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();
        public DbSet<CsvUpload> CsvUploads => Set<CsvUpload>();
        public DbSet<MonitorEntry> MonitorEntries => Set<MonitorEntry>();
        public DbSet<PalletOp> PalletOps => Set<PalletOp>();
        public DbSet<PalletOpItem> PalletOpItems => Set<PalletOpItem>();
        public DbSet<DirectEntry> DirectEntries => Set<DirectEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Table/column names use snake_case (see Program.cs UseSnakeCaseNamingConvention),
            // e.g. Devices→devices, CsvUploads→csv_uploads, DeviceId→device_id.

            modelBuilder.Entity<DeviceRecord>(entity =>
            {
                entity.HasKey(e => e.Id); // surrogate numeric PK (auto-increment)
                entity.Property(e => e.Address).IsRequired().HasMaxLength(64);
                entity.HasIndex(e => e.Address).IsUnique(); // MAC is the stable natural key
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.WorkerId).HasMaxLength(100);
            });

            modelBuilder.Entity<CsvUpload>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.DeviceAddress); // transient carrier, not a column
                entity.Property(e => e.Source).IsRequired().HasMaxLength(20);
                entity.Property(e => e.Type).IsRequired().HasMaxLength(20);
                entity.Property(e => e.TermId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.RawCsv).IsRequired().HasColumnType("longtext");
                // LogDate is a calendar day, not a timestamp → DATE column.
                entity.Property(e => e.LogDate).HasColumnType("date");

                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => new { e.TermId, e.Type });
                // Per-day log filter on the dashboard queries by (Type, LogDate).
                entity.HasIndex(e => new { e.Type, e.LogDate });

                // Device (1) → (*) CsvUploads. Deleting a device removes its uploads.
                entity.HasOne<DeviceRecord>()
                    .WithMany()
                    .HasForeignKey(e => e.DeviceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // CsvUpload (1) → (*) MonitorEntries / PalletOps / DirectEntries (cascade with the upload).
            // Give the string columns explicit lengths so they map to VARCHAR (not LONGTEXT). These
            // are faithful copies of CSV cells (times as "HH:mm:ss", codes, status "0/1/9").
            modelBuilder.Entity<MonitorEntry>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.Property(e => e.StartTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.EndTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.SlipNo).HasMaxLength(64);
                entity.Property(e => e.CustomerCode).HasMaxLength(64);
                entity.Property(e => e.ItemCode).HasMaxLength(64);
                entity.Property(e => e.Status).HasMaxLength(32);
                entity.Property(e => e.StatusCode).HasMaxLength(8);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PalletOp>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.Property(e => e.StartTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.EndTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.OpType).HasMaxLength(32);
                entity.Property(e => e.PlNo).HasMaxLength(64);
                entity.Property(e => e.Customer).HasMaxLength(128);
                entity.Property(e => e.DeliveryRun).HasMaxLength(64);
                entity.Property(e => e.ItemDetailRaw).HasMaxLength(2048);
                entity.Property(e => e.StatusCode).HasMaxLength(8);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PalletOpItem>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.PalletOpId);
                entity.Property(e => e.ItemCode).HasMaxLength(64);
                entity.HasOne(i => i.PalletOp).WithMany(o => o.Items).HasForeignKey(i => i.PalletOpId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<DirectEntry>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UploadId);
                entity.Property(e => e.StartTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.EndTime).HasConversion(TimeOnlyConverter).HasColumnType("time(6)");
                entity.Property(e => e.ShipDate).HasConversion(DateOnlyConverter).HasColumnType("date");
                entity.Property(e => e.Customer).HasMaxLength(128);
                entity.Property(e => e.DeliveryTo).HasMaxLength(128);
                entity.Property(e => e.PartNo).HasMaxLength(64);
                entity.Property(e => e.FactoryCode).HasMaxLength(32);
                entity.Property(e => e.YokooPartNo).HasMaxLength(64);
                entity.HasOne<CsvUpload>().WithMany().HasForeignKey(e => e.UploadId).OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
