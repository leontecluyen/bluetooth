using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Persists received CSV uploads (one row per Bluetooth sync), linked to the sending device,
    /// and normalizes their rows into per-type tables (MonitorEntries / PalletOps + PalletOpItems).
    /// Keeps the raw CSV so the dashboard can show columns exactly as the file's row-1 header.
    /// Newer index of the same (TermId, Type) marks older uploads as <c>Superseded</c>.
    /// </summary>
    public interface ICsvStore
    {
        Task SaveAsync(CsvUpload upload, CancellationToken token = default);
        Task<IReadOnlyList<CsvUpload>> GetByDeviceAsync(string? address, CancellationToken token = default);
        Task<string?> GetRawCsvAsync(Guid uploadId, CancellationToken token = default);
        /// <summary>
        /// Raw CSV text of every upload of <paramref name="typeKey"/> whose log day equals
        /// <paramref name="date"/> (by <c>LogDate</c>, or by the local received date when an older
        /// upload carried no date), oldest first. Used to build the dashboard's per-day log.
        /// </summary>
        Task<IReadOnlyList<string>> GetRawCsvsForDayAsync(string typeKey, DateOnly date, CancellationToken token = default);
        Task ClearAsync(CancellationToken token = default);
    }

    public class CsvStore : ICsvStore
    {
        private const int MaxList = 500;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CsvStore> _logger;

        public CsvStore(IServiceScopeFactory scopeFactory, ILogger<CsvStore> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task SaveAsync(CsvUpload upload, CancellationToken token = default)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.CsvUploads.Add(upload);

                // Normalize rows into typed tables according to the detected type.
                var type = CsvTypes.FromKey(upload.Type);
                if (type == CsvType.MonitorLog)
                {
                    foreach (var e in CsvTypes.ParseMonitor(upload.RawCsv))
                    {
                        e.UploadId = upload.Id;
                        db.MonitorEntries.Add(e);
                    }
                }
                else if (type == CsvType.PalletLog)
                {
                    foreach (var op in CsvTypes.ParsePallet(upload.RawCsv))
                    {
                        op.UploadId = upload.Id;
                        db.PalletOps.Add(op); // Items added via navigation (cascade insert)
                    }
                }
                else if (type == CsvType.DirectLog)
                {
                    foreach (var e in CsvTypes.ParseDirect(upload.RawCsv))
                    {
                        e.UploadId = upload.Id;
                        db.DirectEntries.Add(e);
                    }
                }

                await db.SaveChangesAsync(token);

                // Mark older uploads of the same terminal + type as superseded (snapshot semantics).
                if (!string.IsNullOrEmpty(upload.TermId) && type != CsvType.Unknown && upload.UploadIndex > 0)
                {
                    await db.CsvUploads
                        .Where(u => u.TermId == upload.TermId
                                    && u.Type == upload.Type
                                    && u.UploadIndex < upload.UploadIndex
                                    && !u.Superseded)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.Superseded, true), token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to persist CSV upload from {Addr}: {Msg}", upload.DeviceAddress, ex.Message);
            }
        }

        public async Task<IReadOnlyList<CsvUpload>> GetByDeviceAsync(string? address, CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var query = db.CsvUploads.AsNoTracking();
            if (!string.IsNullOrEmpty(address))
                query = query.Where(u => u.DeviceAddress == address);

            // Metadata only (no RawCsv) so the list stays light.
            return await query
                .OrderByDescending(u => u.ReceivedAtUtc)
                .Take(MaxList)
                .Select(u => new CsvUpload
                {
                    Id = u.Id,
                    DeviceAddress = u.DeviceAddress,
                    ReceivedAtUtc = u.ReceivedAtUtc,
                    Source = u.Source,
                    Device = u.Device,
                    WorkerId = u.WorkerId,
                    Type = u.Type,
                    TermId = u.TermId,
                    UploadIndex = u.UploadIndex,
                    LogDate = u.LogDate,
                    Superseded = u.Superseded,
                    RowCount = u.RowCount,
                    Inserted = u.Inserted,
                    Duplicates = u.Duplicates,
                })
                .ToListAsync(token);
        }

        public async Task<string?> GetRawCsvAsync(Guid uploadId, CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.CsvUploads.AsNoTracking()
                .Where(u => u.Id == uploadId)
                .Select(u => u.RawCsv)
                .FirstOrDefaultAsync(token);
        }

        public async Task<IReadOnlyList<string>> GetRawCsvsForDayAsync(
            string typeKey, DateOnly date, CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // LogDate is stored as the local calendar day at midnight; ReceivedAtUtc is UTC.
            var dayStartLocal = date.ToDateTime(TimeOnly.MinValue);          // local midnight
            var dayEndLocal = dayStartLocal.AddDays(1);
            var dayStartUtc = dayStartLocal.ToUniversalTime();
            var dayEndUtc = dayEndLocal.ToUniversalTime();

            var rows = await db.CsvUploads.AsNoTracking()
                .Where(u => u.Type == typeKey)
                .Where(u => (u.LogDate != null && u.LogDate >= dayStartLocal && u.LogDate < dayEndLocal)
                         || (u.LogDate == null && u.ReceivedAtUtc >= dayStartUtc && u.ReceivedAtUtc < dayEndUtc))
                .OrderBy(u => u.ReceivedAtUtc)
                .Select(u => u.RawCsv)
                .ToListAsync(token);

            _logger.LogDebug("Per-day log query: type={Type} date={Date:yyyy-MM-dd} → {Count} uploads.",
                typeKey, dayStartLocal, rows.Count);
            return rows;
        }

        public async Task ClearAsync(CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.CsvUploads.ExecuteDeleteAsync(token); // cascades to typed tables
        }
    }
}
