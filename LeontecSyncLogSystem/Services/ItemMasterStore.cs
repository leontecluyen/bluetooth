using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Owns the DB copy of the 品目マスタ (<c>item_master</c> table). Two responsibilities, both driven
    /// at startup by <c>Program</c>:
    /// <list type="number">
    ///   <item><see cref="EnsureSchemaAsync"/> — create the table if it doesn't exist. Needed because
    ///     <c>EnsureCreated()</c> only builds tables on a FRESH database and never alters an existing
    ///     one, so adding a table to a DB that already has data requires an explicit
    ///     <c>CREATE TABLE IF NOT EXISTS</c>.</item>
    ///   <item><see cref="UpsertFromCsvAsync"/> — import the item master CSV (<see cref="IMasterStore"/>,
    ///     seeded from the Android assets) as an <b>UPSERT keyed by 品目コード</b>: INSERT new codes,
    ///     UPDATE existing ones, and <b>never DELETE</b>. Runs on every startup (idempotent), so it is
    ///     safe to re-run after a future feature/schema change without losing data already in the table.</item>
    /// </list>
    /// The supply export uses <see cref="GetAllNamesAsync"/> to resolve a 品番 to its ヨコオ品番.
    /// </summary>
    public interface IItemMasterStore
    {
        /// <summary>Create the <c>item_master</c> table if it is missing (idempotent). Never throws.</summary>
        Task EnsureSchemaAsync(CancellationToken token = default);

        /// <summary>
        /// Import the item master CSV as an UPSERT keyed by 品目コード (INSERT new / UPDATE existing,
        /// NEVER delete): a blank incoming cell keeps the already-stored value. Returns the number of
        /// rows inserted-or-updated (0 if there was nothing to import or the import failed). Never
        /// throws — a DB/CSV failure is logged and swallowed so startup is never blocked.
        /// </summary>
        Task<int> UpsertFromCsvAsync(CancellationToken token = default);

        /// <summary>
        /// Every non-empty 品目名称 in the table (the ヨコオ品番 lookup matches against these in memory).
        /// Returns an empty list on any DB failure.
        /// </summary>
        Task<IReadOnlyList<string>> GetAllNamesAsync(CancellationToken token = default);
    }

    /// <inheritdoc cref="IItemMasterStore"/>
    public sealed class ItemMasterStore : IItemMasterStore
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMasterStore _masterStore;
        private readonly ILogger<ItemMasterStore> _logger;

        public ItemMasterStore(
            IServiceScopeFactory scopeFactory, IMasterStore masterStore, ILogger<ItemMasterStore> logger)
        {
            _scopeFactory = scopeFactory;
            _masterStore = masterStore;
            _logger = logger;
        }

        public async Task EnsureSchemaAsync(CancellationToken token = default)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Keep this schema identical to the EF model (AppDbContext.OnModelCreating → ItemMaster)
                // so a fresh DB (built by EnsureCreated) and an existing DB (patched here) match.
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS `item_master` (" +
                    "`id` bigint NOT NULL AUTO_INCREMENT, " +
                    "`code` varchar(64) NULL, " +
                    "`name` varchar(256) NULL, " +
                    "`box_type` varchar(64) NULL, " +
                    "`sub_name` varchar(256) NULL, " +
                    "PRIMARY KEY (`id`), " +
                    "KEY `ix_item_master_code` (`code`)" +
                    ") DEFAULT CHARSET=utf8mb4;", token);
                _logger.LogInformation("item_master table ensured (created if it was missing).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure the item_master table exists.");
            }
        }

        public async Task<int> UpsertFromCsvAsync(CancellationToken token = default)
        {
            try
            {
                var csv = _masterStore.Load(MasterKind.Item).Csv;
                var rows = ParseItemMasterCsv(csv);
                if (rows.Count == 0)
                {
                    _logger.LogWarning("item_master CSV had no data rows; nothing to import.");
                    return 0;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Load existing rows (tracked) and index by 品目コード so we can UPDATE in place.
                // Never delete: rows present in the DB but absent from the CSV are left untouched.
                var byCode = new Dictionary<string, ItemMaster>(StringComparer.Ordinal);
                foreach (var e in await db.ItemMasters.ToListAsync(token))
                    if (!string.IsNullOrEmpty(e.Code)) byCode[e.Code] = e; // last wins on pre-existing dup codes

                int inserted = 0, updated = 0;
                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.Code)) continue; // can't upsert a row with no key
                    if (byCode.TryGetValue(row.Code, out var cur))
                    {
                        // UPDATE — overwrite a field only when the incoming cell is non-empty, so a blank
                        // CSV cell can't wipe an already-registered 品目名称 / 箱種 / 品目名称_2.
                        if (!string.IsNullOrEmpty(row.Name)) cur.Name = row.Name;
                        if (!string.IsNullOrEmpty(row.BoxType)) cur.BoxType = row.BoxType;
                        if (!string.IsNullOrEmpty(row.SubName)) cur.SubName = row.SubName;
                        updated++;
                    }
                    else
                    {
                        db.ItemMasters.Add(row);
                        byCode[row.Code] = row; // a later CSV row with the same code updates, not re-inserts
                        inserted++;
                    }
                }

                await db.SaveChangesAsync(token);
                _logger.LogInformation(
                    "item_master upsert from CSV: {Inserted} inserted, {Updated} updated (never deletes).",
                    inserted, updated);
                return inserted + updated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert item_master from the CSV.");
                return 0;
            }
        }

        public async Task<IReadOnlyList<string>> GetAllNamesAsync(CancellationToken token = default)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.ItemMasters.AsNoTracking()
                    .Where(i => i.Name != null && i.Name != "")
                    .Select(i => i.Name)
                    .ToListAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read item_master names: {Msg}", ex.Message);
                return new List<string>();
            }
        }

        /// <summary>
        /// Parse the item master CSV (header <c>品目コード,品目名称,箱種,品目名称_2</c>). Skips the header row
        /// and any row whose first two cells (code + name) are both blank. Tolerant of a missing trailing
        /// column (箱種 / 品目名称_2 are often empty in the source).
        /// </summary>
        private static List<ItemMaster> ParseItemMasterCsv(string csv)
        {
            var result = new List<ItemMaster>();
            if (string.IsNullOrWhiteSpace(csv)) return result;

            var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool headerSkipped = false;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!headerSkipped)
                {
                    // Row 1 is the header (starts with 品目コード); skip it and continue.
                    headerSkipped = true;
                    if (raw.StartsWith("品目コード")) continue;
                    // No recognizable header — treat this row as data (fall through).
                }

                var cells = CsvTypes.SplitCsv(raw);
                string code = cells.Count > 0 ? cells[0].Trim() : "";
                string name = cells.Count > 1 ? cells[1].Trim() : "";
                if (code.Length == 0 && name.Length == 0) continue;

                result.Add(new ItemMaster
                {
                    Code = code,
                    Name = name,
                    BoxType = cells.Count > 2 ? cells[2].Trim() : null,
                    SubName = cells.Count > 3 ? cells[3].Trim() : null,
                });
            }
            return result;
        }
    }
}
