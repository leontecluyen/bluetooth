using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Lightweight, read-only view of a persisted CSV upload for the dashboard list. Carries the
    /// sending device's <see cref="Address"/> (resolved via a join on <c>DeviceId</c>) so the UI can
    /// filter by device without loading the heavy <c>RawCsv</c>.
    /// </summary>
    public sealed record CsvUploadInfo(
        long Id,
        string Address,
        DateTime ReceivedAtUtc,
        string Source,
        string Type,
        string TermId,
        int UploadIndex,
        DateTime? LogDate,
        bool Superseded,
        int RowCount);

    /// <summary>
    /// Persists received CSV uploads (one row per Bluetooth sync), linked to the sending device by
    /// <c>DeviceId</c>, and normalizes their rows into per-type tables (MonitorEntries / PalletOps +
    /// PalletOpItems / DirectEntries). Keeps the raw CSV so the dashboard can show columns exactly as
    /// the file's row-1 header. A newer index of the same (TermId, Type) marks older uploads
    /// <c>Superseded</c>; the SAME index replaces the prior upload.
    /// </summary>
    public interface ICsvStore
    {
        Task SaveAsync(CsvUpload upload, CancellationToken token = default);
        Task<IReadOnlyList<CsvUploadInfo>> GetByDeviceAsync(string? address, CancellationToken token = default);
        Task<string?> GetRawCsvAsync(long uploadId, CancellationToken token = default);
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

                // Resolve the sending device (its row is upserted just before this call) to its
                // surrogate key. Without a device we cannot satisfy the FK, so skip (best-effort).
                var deviceId = await db.Devices.AsNoTracking()
                    .Where(d => d.Address == upload.DeviceAddress)
                    .Select(d => (long?)d.Id)
                    .FirstOrDefaultAsync(token);
                if (deviceId is null)
                {
                    _logger.LogWarning(
                        "No device row for {Addr}; skipping CSV persist.", upload.DeviceAddress);
                    return;
                }
                upload.DeviceId = deviceId.Value;

                // Phase 1: insert the upload so its auto-increment Id is assigned.
                db.CsvUploads.Add(upload);
                await db.SaveChangesAsync(token);

                // Phase 2: normalize rows into typed tables, keyed on the now-known upload.Id.
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

                if (!string.IsNullOrEmpty(upload.TermId) && type != CsvType.Unknown && upload.UploadIndex > 0)
                {
                    // Resend with the SAME index = REPLACE: drop any prior upload of the same
                    // (term, type, index) so the day view shows one copy, not a duplicate. The phone
                    // re-sends a backup file under its original name precisely to overwrite the old one.
                    // (EF Core 3.1 has no ExecuteDelete; load + RemoveRange, cascades via DB FK.)
                    var toReplace = await db.CsvUploads
                        .Where(u => u.TermId == upload.TermId
                                    && u.Type == upload.Type
                                    && u.UploadIndex == upload.UploadIndex
                                    && u.Id != upload.Id)
                        .ToListAsync(token);
                    if (toReplace.Count > 0)
                    {
                        db.CsvUploads.RemoveRange(toReplace); // cascades to typed tables (ON DELETE CASCADE)
                        await db.SaveChangesAsync(token);
                    }

                    // Mark older uploads (lower index) of the same terminal + type as superseded.
                    // (EF Core 3.1 has no ExecuteUpdate; load the rows, flip the flag, save.)
                    var toSupersede = await db.CsvUploads
                        .Where(u => u.TermId == upload.TermId
                                    && u.Type == upload.Type
                                    && u.UploadIndex < upload.UploadIndex
                                    && !u.Superseded)
                        .ToListAsync(token);
                    if (toSupersede.Count > 0)
                    {
                        foreach (var u in toSupersede) u.Superseded = true;
                        await db.SaveChangesAsync(token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to persist CSV upload from {Addr}: {Msg}", upload.DeviceAddress, ex.Message);
            }
        }

        public async Task<IReadOnlyList<CsvUploadInfo>> GetByDeviceAsync(string? address, CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Join Devices to carry the MAC address (used by the UI to filter by device) and,
            // when requested, to filter the list to one device.
            var query =
                from u in db.CsvUploads.AsNoTracking()
                join d in db.Devices.AsNoTracking() on u.DeviceId equals d.Id
                where address == null || address == "" || d.Address == address
                orderby u.ReceivedAtUtc descending
                select new CsvUploadInfo(
                    u.Id, d.Address, u.ReceivedAtUtc, u.Source, u.Type,
                    u.TermId, u.UploadIndex, u.LogDate, u.Superseded, u.RowCount);

            return await query.Take(MaxList).ToListAsync(token);
        }

        public async Task<string?> GetRawCsvAsync(long uploadId, CancellationToken token = default)
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
            // EF Core 3.1 has no ExecuteDelete; a raw DELETE clears the table and cascades to the typed
            // tables via their ON DELETE CASCADE FKs (snake_case table name).
            await db.Database.ExecuteSqlRawAsync("DELETE FROM `csv_uploads`", token);
        }
    }
}
