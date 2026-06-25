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
        /// Wipes all logs, CSV uploads and devices from the DB and clears the live client list.
        /// Destructive — the caller (dashboard) must confirm first. Returns log rows deleted.
        /// </summary>
        public async Task<int> ClearAllAsync(CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int deleted = await db.Logs.ExecuteDeleteAsync(token);
            await db.CsvUploads.ExecuteDeleteAsync(token);
            await db.Devices.ExecuteDeleteAsync(token);
            _status.ClearClients();
            _logger.LogWarning(
                "CLEAR ALL: deleted {Count} log rows + CSV uploads + Devices; cleared the live client list.", deleted);
            return deleted;
        }

        public async Task<StatusDto> GetSnapshotAsync(CancellationToken token = default)
        {
            var now = DateTime.UtcNow;
            // "Today" is judged by the CSV filename date (LogDate), which is a LOCAL calendar day.
            var todayLocal = DateTime.Today;
            var tomorrowLocal = todayLocal.AddDays(1);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
                Logs = new LogsDto
                {
                    // Count CSV log rows (the real data) from the current, non-superseded uploads —
                    // SyncLogs is the legacy table and is empty now, which is why this read 0.
                    Total = await db.CsvUploads.Where(u => !u.Superseded)
                        .SumAsync(u => (int?)u.RowCount, token) ?? 0,
                    Today = await db.CsvUploads
                        .Where(u => !u.Superseded && u.LogDate != null
                                 && u.LogDate >= todayLocal && u.LogDate < tomorrowLocal)
                        .SumAsync(u => (int?)u.RowCount, token) ?? 0,
                },
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
            if (day is DateOnly d)
                uploads = uploads.Where(u => u.LogDate.HasValue
                                          && DateOnly.FromDateTime(u.LogDate.Value) == d).ToList();
            return uploads.Select(u => new ReceivedCsvDto
            {
                Id = u.Id,
                ReceivedAtUtc = u.ReceivedAtUtc,
                Source = u.Source,
                Device = u.Device,
                Address = u.DeviceAddress,
                WorkerId = u.WorkerId,
                Type = u.Type,
                TermId = u.TermId,
                UploadIndex = u.UploadIndex,
                LogDate = u.LogDate,
                Superseded = u.Superseded,
                RowCount = u.RowCount,
                Inserted = u.Inserted,
                Duplicates = u.Duplicates,
            }).ToList();
        }

        /// <summary>
        /// The selected CSV rendered as a table — headers from row 1, then data rows — exactly
        /// as received. Type-agnostic so each CSV type shows its own columns.
        /// </summary>
        public async Task<CsvTableDto> GetCsvTableAsync(Guid uploadId, CancellationToken token = default)
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
        ///  • <b>monitor</b>: hide 状態 = 9 (削除); show the rest (状態 = 0 正常).
        ///  • <b>pallet</b>: key = (PLNo., 顧客, 納入便). Hide 状態 = 9; among the surviving rows
        ///    (状態 0/1) of the same key, keep only the one with the latest 終了時刻.
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
                    int st = Idx("状態");
                    if (st < 0) return;
                    dto.Rows = dto.Rows.Where(r => Cell(r, st) != "9").ToList();
                    break;
                }
                case "pallet_log":
                {
                    int st = Idx("状態"), pl = Idx("PLNo."), cu = Idx("顧客"), ru = Idx("納入便"), en = Idx("終了時刻");
                    if (st < 0 || pl < 0 || cu < 0 || ru < 0) return;
                    var kept = new Dictionary<string, string[]>();
                    var order = new List<string>();
                    foreach (var r in dto.Rows)
                    {
                        if (Cell(r, st) == "9") continue;                 // 削除 → hide
                        var key = $"{Cell(r, pl)}|{Cell(r, cu)}|{Cell(r, ru)}";
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
