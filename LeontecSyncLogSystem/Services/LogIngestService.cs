using Microsoft.EntityFrameworkCore;
using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;

namespace LeontecSyncLogSystem.Services
{
    public record IngestResult(int Received, int Inserted, int Duplicates);

    public interface ILogIngestService
    {
        Task<IngestResult> IngestAsync(IReadOnlyList<LogEntry> entries, CancellationToken token);
    }

    /// <summary>
    /// Persists log entries with idempotent, deduplicated inserts keyed on <see cref="LogEntry.LogId"/>.
    ///
    /// Strategy (provider-agnostic equivalent of "ON CONFLICT DO NOTHING"):
    ///  1. Collapse duplicate LogIds within the incoming batch.
    ///  2. Query which LogIds already exist and drop those.
    ///  3. SaveChanges; if a concurrent insert still produces a unique-key violation,
    ///     fall back to inserting the surviving rows one-by-one so a single clash
    ///     never loses the rest of the batch.
    /// </summary>
    public class LogIngestService : ILogIngestService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LogIngestService> _logger;

        public LogIngestService(IServiceScopeFactory scopeFactory, ILogger<LogIngestService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<IngestResult> IngestAsync(IReadOnlyList<LogEntry> entries, CancellationToken token)
        {
            if (entries.Count == 0)
                return new IngestResult(0, 0, 0);

            // 1. De-dup within the batch (keep first occurrence of each LogId).
            var batch = entries
                .GroupBy(e => e.LogId)
                .Select(g => g.First())
                .ToList();

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 2. Drop LogIds already present in the DB.
            var ids = batch.Select(e => e.LogId).ToList();
            var existing = await db.Logs
                .Where(l => ids.Contains(l.LogId))
                .Select(l => l.LogId)
                .ToListAsync(token);
            var existingSet = existing.ToHashSet();

            var toInsert = batch.Where(e => !existingSet.Contains(e.LogId)).ToList();

            // "Duplicates" = every received row that did NOT become a new insert. This counts
            // BOTH in-file duplicates (same LogId twice in this CSV, collapsed at step 1) AND
            // cross-file duplicates (LogId already in the DB), so the dashboard number is intuitive.
            if (toInsert.Count == 0)
                return new IngestResult(entries.Count, 0, entries.Count);

            db.Logs.AddRange(toInsert);

            try
            {
                int inserted = await db.SaveChangesAsync(token);
                return new IngestResult(entries.Count, inserted, entries.Count - inserted);
            }
            catch (DbUpdateException ex)
            {
                // Race condition: another channel inserted the same LogId between our
                // existence check and SaveChanges. Retry row-by-row, skipping clashes.
                _logger.LogWarning(ex,
                    "Batch insert hit a unique-key conflict; retrying row-by-row.");
                db.ChangeTracker.Clear();
                return await InsertOneByOneAsync(toInsert, entries.Count, token);
            }
        }

        private async Task<IngestResult> InsertOneByOneAsync(
            List<LogEntry> rows, int received, CancellationToken token)
        {
            int inserted = 0;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var row in rows)
            {
                if (await db.Logs.AnyAsync(l => l.LogId == row.LogId, token))
                    continue;

                db.Logs.Add(row);
                try
                {
                    await db.SaveChangesAsync(token);
                    inserted++;
                }
                catch (DbUpdateException)
                {
                    db.Entry(row).State = EntityState.Detached;
                }
            }

            // received - inserted = in-file dups + already-in-DB + race clashes.
            return new IngestResult(received, inserted, received - inserted);
        }
    }
}
