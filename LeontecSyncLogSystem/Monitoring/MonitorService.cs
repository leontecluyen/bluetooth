using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;
using LeontecSyncLogSystem.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeontecSyncLogSystem.Monitoring
{
    /// <summary>
    /// Builds a point-in-time snapshot of the running system (Bluetooth server state,
    /// connected clients, log totals) from the in-process <see cref="ServiceStatus"/> and the
    /// database, plus on-demand CSV queries (uploads per device, rows per upload) backed by
    /// <see cref="ICsvStore"/>. Consumed directly by the dashboard and by <c>GET /api/status</c>.
    /// </summary>
    public sealed class MonitorService
    {
        private readonly ServiceStatus _status;
        private readonly ICsvStore _csvStore;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MonitorService> _logger;

        public MonitorService(
            ServiceStatus status, ICsvStore csvStore, IServiceScopeFactory scopeFactory, ILogger<MonitorService> logger)
        {
            _status = status;
            _csvStore = csvStore;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// Wipes all CSV uploads and devices from the DB and clears the live client list.
        /// Destructive — the caller (dashboard) must confirm first. Returns CSV uploads deleted.
        /// (Deleting an upload cascades to its normalized MonitorEntries/PalletOps/DirectEntries.)
        /// </summary>
        public async Task<int> ClearAllAsync(CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // EF Core 3.1 has no ExecuteDelete; count first, then raw DELETE (cascades to typed tables).
            int deleted = await db.CsvUploads.CountAsync(token);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM `csv_uploads`", token);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM `devices`", token);
            _status.ClearClients();
            _logger.LogWarning(
                "CLEAR ALL: deleted {Count} CSV uploads (+ normalized rows) + Devices; cleared the live client list.", deleted);
            return deleted;
        }

        /// <summary>
        /// Lightweight liveness probe for the EXTERNAL MySQL server: opens a connection and reports
        /// whether it succeeds. Never throws — a failure (server down, wrong credentials in mysql.xml,
        /// etc.) is logged at Warning and returned as <c>false</c> so the dashboard can show
        /// "MySQL: disconnected" without erroring the whole refresh.
        /// </summary>
        public async Task<bool> IsDbConnectedAsync(CancellationToken token = default)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.Database.CanConnectAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MySQL connectivity check failed: {Msg}", ex.Message);
                return false;
            }
        }

        public async Task<StatusDto> GetSnapshotAsync(CancellationToken token = default)
        {
            var now = DateTime.UtcNow;
            // "Today" is judged by the CSV filename date (LogDate), which is a LOCAL calendar day.
            var todayLocal = DateTime.Today;
            var tomorrowLocal = todayLocal.AddDays(1);

            // The log totals are the ONLY DB-backed part of the snapshot; everything else comes from
            // the in-memory ServiceStatus. The external MySQL may be down, so a query failure must NOT
            // fail the whole snapshot — degrade to 0/0 (and the toolbar shows "MySQL: disconnected")
            // instead of throwing a scary EF error onto the dashboard's status line.
            var logs = new LogsDto();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Count CSV log rows (the real data) from the current, non-superseded uploads.
                logs.Total = await db.CsvUploads.Where(u => !u.Superseded)
                    .SumAsync(u => (int?)u.RowCount, token) ?? 0;
                logs.Today = await db.CsvUploads
                    .Where(u => !u.Superseded && u.LogDate != null
                             && u.LogDate >= todayLocal && u.LogDate < tomorrowLocal)
                    .SumAsync(u => (int?)u.RowCount, token) ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Log totals unavailable (external MySQL not reachable?): {Msg}", ex.Message);
            }

            return new StatusDto
            {
                ServerTimeUtc = now,
                StartedUtc = _status.StartedUtc,
                UptimeSeconds = (long)(now - _status.StartedUtc).TotalSeconds,
                BtServer = new BtServerDto
                {
                    Listening = _status.ServerListening,
                    RadioName = _status.RadioName,
                    ServiceName = _status.ServiceName,
                    LastError = _status.LastError,
                },
                Clients = _status.Clients.Select(c => new ClientDto
                {
                    Name = c.Name,
                    Address = c.Address,
                    WorkerId = c.WorkerId,
                    Connected = c.Connected,
                    Online = c.IsOnline(now),
                    LastFrameUtc = c.LastFrameUtc,
                    LastSeenUtc = c.LastSeenUtc,
                    LastHeartbeatUtc = c.LastHeartbeatUtc,
                    FramesReceived = c.FramesReceived,
                    RecordsIngested = c.RecordsIngested,
                    Sessions = c.Sessions,
                    Heartbeats = c.Heartbeats,
                }).ToList(),
                Logs = logs,
            };
        }

        /// <summary>
        /// CSV uploads of a device (null = all), newest first — for the dashboard list. When
        /// <paramref name="day"/> is given, only uploads whose <b>filename date</b> (<c>LogDate</c>,
        /// parsed from the <c>yyyyMMdd</c> in the upload filename) matches it are returned — NOT the
        /// received/creation date — so the bottom-left list follows the date filter by file day.
        /// Legacy uploads with no filename date (<c>LogDate == null</c>) are excluded when filtering.
        /// </summary>
        public async Task<List<ReceivedCsvDto>> GetCsvsForDeviceAsync(
            string? deviceAddress, DateOnly? day = null, CancellationToken token = default)
        {
            var uploads = await _csvStore.GetByDeviceAsync(deviceAddress, token);
            IEnumerable<CsvUploadInfo> filtered = uploads;
            if (day is DateOnly d)
                filtered = uploads.Where(u => u.LogDate.HasValue
                                           && DateOnly.FromDateTime(u.LogDate.Value) == d);
            return filtered.Select(u => new ReceivedCsvDto
            {
                Id = u.Id,
                ReceivedAtUtc = u.ReceivedAtUtc,
                Source = u.Source,
                Address = u.Address,
                Type = u.Type,
                TermId = u.TermId,
                UploadIndex = u.UploadIndex,
                LogDate = u.LogDate,
                Superseded = u.Superseded,
                RowCount = u.RowCount,
            }).ToList();
        }

        /// <summary>
        /// The selected CSV rendered as a table — headers from row 1, then data rows — exactly
        /// as received. Type-agnostic so each CSV type shows its own columns.
        /// </summary>
        public async Task<CsvTableDto> GetCsvTableAsync(long uploadId, CancellationToken token = default)
        {
            var raw = await _csvStore.GetRawCsvAsync(uploadId, token);
            var dto = new CsvTableDto();
            if (string.IsNullOrWhiteSpace(raw)) return dto;

            AppendCsv(dto, raw);
            return dto;
        }

        /// <summary>
        /// The full log of one <paramref name="typeKey"/> for one calendar <paramref name="date"/>,
        /// aggregated across <b>all</b> uploads received that day (including superseded ones, per the
        /// dashboard's per-day view). Columns are taken from the first upload's row-1 header; rows
        /// are concatenated oldest-upload-first. Type-agnostic so each type shows its own columns.
        /// </summary>
        public async Task<CsvTableDto> GetDayLogAsync(string typeKey, DateOnly date, CancellationToken token = default)
        {
            var raws = await _csvStore.GetRawCsvsForDayAsync(typeKey, date, token);
            var dto = new CsvTableDto();
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                AppendCsv(dto, raw);
            }
            int rawRows = dto.Rows.Count;
            ApplyDisplayFilter(typeKey, dto);
            _logger.LogDebug(
                "Per-day log built: type={Type} date={Date:yyyy-MM-dd} → {Shown}/{Raw} rows from {Uploads} uploads (after display filter).",
                typeKey, date.ToDateTime(TimeOnly.MinValue), dto.Rows.Count, rawRows, raws.Count);
            return dto;
        }

        /// <summary>
        /// Apply the per-type "what to show" rules to the aggregated rows (columns located by their
        /// row-1 header so this survives column reordering):
        ///  • <b>monitor</b>: a 状態 = 9 (削除) row cancels the earlier 状態 = 0 (正常) row with the
        ///    same 入出庫伝票番号 (Android writes the delete row keeping every field, only 状態 flips →
        ///    match by invoiceNo). Hide BOTH the delete row AND the original row it cancels; each 削除
        ///    row cancels exactly one 正常 row (oldest first). Show whatever survives.
        ///  • <b>pallet</b>: key = (PLNo., 顧客, 納入便). A 状態 = 9 (削除) means the whole pallet was
        ///    deleted from the DB → hide EVERY row of that key (the 削除 row AND its 正常/移動 rows).
        ///    Among the surviving rows (状態 0/1) of a non-deleted key, keep only the latest 終了時刻.
        ///  • <b>direct</b> / others: show everything (no 状態 column).
        /// </summary>
        private static void ApplyDisplayFilter(string typeKey, CsvTableDto dto)
        {
            int Idx(string header) => dto.Headers.FindIndex(h => h.Trim() == header);
            static string Cell(string[] r, int i) => (i >= 0 && i < r.Length) ? r[i].Trim() : "";

            switch (typeKey)
            {
                case "monitor_log":
                {
                    // Một dòng 状態=9 (削除) là bản ghi "hủy" dòng 状態=0 (正常) cùng 入出庫伝票番号 đã ghi
                    // trước đó — Android ghi dòng xóa giữ nguyên mọi field, chỉ đổi 状態 (xem
                    // StockViewModel.deleteSelectedStock) → ghép theo invoiceNo. Ẩn CẢ dòng xóa LẪN dòng
                    // gốc bị nó hủy; mỗi dòng 9 hủy đúng 1 dòng 0 (cũ nhất trước). Áp cho cả list lẫn Export.
                    int st = Idx("状態"), inv = Idx("入出庫伝票番号");
                    if (st < 0) return;
                    if (inv < 0)
                    {
                        // Không định vị được khóa ghép → giữ hành vi cũ: chỉ ẩn dòng 削除.
                        dto.Rows = dto.Rows.Where(r => Cell(r, st) != "9").ToList();
                        break;
                    }
                    // Pha 1: đếm số dòng xóa (状態=9) theo 入出庫伝票番号 (đếm trước nên thứ tự 0/9 không ảnh hưởng).
                    var pendingDeletes = new Dictionary<string, int>();
                    foreach (var r in dto.Rows)
                    {
                        if (Cell(r, st) != "9") continue;
                        var key = Cell(r, inv);
                        pendingDeletes[key] = pendingDeletes.TryGetValue(key, out var c) ? c + 1 : 1;
                    }
                    // Pha 2: bỏ mọi dòng 削除; với mỗi invoiceNo còn "nợ" xóa, bỏ dòng 正常 cũ nhất tương ứng.
                    var kept = new List<string[]>(dto.Rows.Count);
                    foreach (var r in dto.Rows)
                    {
                        if (Cell(r, st) == "9") continue;                    // dòng xóa: luôn ẩn
                        var key = Cell(r, inv);
                        if (Cell(r, st) == "0" && pendingDeletes.TryGetValue(key, out var c) && c > 0)
                        {
                            pendingDeletes[key] = c - 1;                     // dòng gốc bị 1 dòng xóa hủy → ẩn
                            continue;
                        }
                        kept.Add(r);
                    }
                    dto.Rows = kept;
                    break;
                }
                case "pallet_log":
                {
                    int st = Idx("状態"), pl = Idx("PLNo."), cu = Idx("顧客"), ru = Idx("納入便"), en = Idx("終了時刻");
                    if (st < 0 || pl < 0 || cu < 0 || ru < 0) return;
                    string KeyOf(string[] r) => $"{Cell(r, pl)}|{Cell(r, cu)}|{Cell(r, ru)}";

                    // Một dòng 状態=9 (削除) nghĩa là CẢ pallet (khóa PLNo.+顧客+納入便) đã bị xóa khỏi DB
                    // (ShippingActivity.performDeletePallet xóa pallet + invoices). Khác monitor (hủy theo
                    // từng dòng invoiceNo), pallet có nhiều dòng/khóa theo vòng đời (積込 0 / 移動 1) nên hủy
                    // theo KHÓA: khóa nào có 削除 thì ẩn TOÀN BỘ dòng của nó (cả 削除 lẫn 正常/移動) → pallet
                    // đã xóa biến mất hoàn toàn khỏi list + Export.
                    var deletedKeys = new HashSet<string>();
                    foreach (var r in dto.Rows)
                        if (Cell(r, st) == "9") deletedKeys.Add(KeyOf(r));

                    var kept = new Dictionary<string, string[]>();
                    var order = new List<string>();
                    foreach (var r in dto.Rows)
                    {
                        if (Cell(r, st) == "9") continue;                 // 削除 → hide
                        var key = KeyOf(r);
                        if (deletedKeys.Contains(key)) continue;          // pallet đã bị xóa → ẩn luôn 正常/移動
                        if (!kept.TryGetValue(key, out var cur)) { kept[key] = r; order.Add(key); }
                        else if (string.CompareOrdinal(Cell(r, en), Cell(cur, en)) > 0) kept[key] = r; // newer 終了時刻 wins
                    }
                    dto.Rows = order.Select(k => kept[k]).ToList();
                    break;
                }
                // direct_log / legacy / unknown: show everything as-is.
            }
        }

        /// <summary>
        /// Parse one raw CSV (header on row 1) and append its data rows to <paramref name="dto"/>.
        /// The first CSV seen sets the headers; later CSVs of the same type contribute rows only.
        /// </summary>
        private static void AppendCsv(CsvTableDto dto, string raw)
        {
            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool first = true;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = CsvTypes.SplitCsv(line);
                if (first)
                {
                    first = false;
                    if (dto.Headers.Count == 0)
                        dto.Headers = fields.Select(f => f.Trim()).ToList();
                    // else: skip this CSV's header row (same type → same columns).
                }
                else
                {
                    dto.Rows.Add(fields.ToArray());
                }
            }
        }
    }
}
