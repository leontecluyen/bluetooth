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

            // Columns are PINNED to the type's canonical DISPLAY header (see CanonicalHeaders), NOT
            // taken from "whichever upload arrived first" nor from the phone's wire layout. Older builds
            // shipped drifting monitor/pallet headers (8 vs 9 vs 10 cols, a duplicated 状態, an extra 操作
            // col …); deriving the grid from the first upload made the visible columns — and therefore
            // Export — jump between renders. The display header may also intentionally differ from the
            // wire CSV (e.g. direct is uploaded 11-col but shown 10-col, 出荷日-first). Each upload's rows
            // are re-projected onto the canonical columns BY HEADER NAME, so column reordering / extra /
            // missing columns can't misalign data and the filter always reads the numeric 状態 code
            // column (never a look-alike text column).
            var canonical = CanonicalHeaders(typeKey);
            if (canonical is not null)
            {
                dto.Headers = canonical.ToList();
                foreach (var raw in raws)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    AppendCsvProjected(dto, raw, canonical);
                }
            }
            else
            {
                // Unknown type (no canonical layout): fall back to "first upload sets the header".
                foreach (var raw in raws)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    AppendCsv(dto, raw);
                }
            }

            int rawRows = dto.Rows.Count;
            ApplyDisplayFilter(typeKey, dto);

            // monitor & pallet: 状態 (the trailing 0/9/1 code) is only needed to COMPUTE the display
            // filter above — the user does not want it shown. Drop the column now that filtering is
            // done so it appears neither in the grid nor in Export (Export just additionally drops the
            // display-only "#" ordinal). direct has no 状態, so it's untouched.
            if (typeKey == "monitor_log" || typeKey == "pallet_log")
                RemoveColumn(dto, "状態");

            // Finally sort the (filtered) rows by 終了時刻 (completion time) DESCENDING — newest-completed
            // first — for all 3 types. The day-log grid binds these rows in order, and Export serialises
            // the same bound table, so grid + Export share this ordering. No-op if 終了時刻 is absent.
            SortByEndTimeDesc(dto);

            _logger.LogDebug(
                "Per-day log built: type={Type} date={Date:yyyy-MM-dd} → {Shown}/{Raw} rows from {Uploads} uploads (after display filter, sorted by 終了時刻 desc).",
                typeKey, date.ToDateTime(TimeOnly.MinValue), dto.Rows.Count, rawRows, raws.Count);
            return dto;
        }

        /// <summary>
        /// Sort <paramref name="dto"/>'s rows by the <c>終了時刻</c> (completion time) column DESCENDING —
        /// newest-completed first. Stable (<see cref="Enumerable.OrderByDescending{TSource,TKey}"/>) so
        /// rows with an equal (or missing) time keep their prior creation order. No-op when the column is
        /// absent (e.g. an unknown-type log). Blank/unparseable times sort last.
        /// </summary>
        private static void SortByEndTimeDesc(CsvTableDto dto)
        {
            int idx = dto.Headers.FindIndex(h => h.Trim() == "終了時刻");
            if (idx < 0) return;
            dto.Rows = dto.Rows
                .OrderByDescending(r => EndTimeKey(idx < r.Length ? r[idx] : ""))
                .ToList();
        }

        /// <summary>Parse a <c>終了時刻</c> cell to a sortable <see cref="TimeSpan"/>; blank/invalid → MinValue (sorts last in desc).</summary>
        private static TimeSpan EndTimeKey(string cell)
            => TimeSpan.TryParse((cell ?? "").Trim(), out var t) ? t : TimeSpan.MinValue;

        /// <summary>
        /// Build the 直送 (direct) "supply" export (補給データ出力) for one <paramref name="date"/>: aggregate
        /// all direct uploads of the day, keep only <b>トヨタ</b> rows (顧客 == "トヨタ"), and project onto the
        /// fixed 5-column supply layout <c>出荷日, 品番, 収容数(数量), 工場コード, ヨコオ品番</c>. The 工場コード value
        /// is remapped by <see cref="MapFactoryCode"/> (its 5th–6th chars: "…T3…" → "A6", "…L3…" → "A9",
        /// anything else → ""). Reads the RAW uploaded CSV (11-col wire layout) so it can still see
        /// 収容数 / ヨコオ品番 — columns the normal 10-col day-log display drops. Independent of the day-log
        /// display filter (direct shows all rows anyway).
        /// </summary>
        public async Task<CsvTableDto> GetDirectSupplyExportAsync(DateOnly date, CancellationToken token = default)
        {
            var raws = await _csvStore.GetRawCsvsForDayAsync("direct_log", date, token);

            // Pull the source columns we need BY HEADER NAME — 顧客 (Toyota filter), 終了時刻 (sort key)
            // plus 収容数 / ヨコオ品番 which the 10-col display layout omits. AppendCsvProjected tolerates
            // column reordering.
            var srcCols = new[] { "顧客", "出荷日", "品番", "収容数", "工場コード", "ヨコオ品番", "終了時刻" };
            var src = new CsvTableDto { Headers = srcCols.ToList() };
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                AppendCsvProjected(src, raw, srcCols);
            }

            var dto = new CsvTableDto
            {
                Headers = new List<string> { "出荷日", "品番", "収容数(数量)", "工場コード", "ヨコオ品番" },
            };
            const int cust = 0, ship = 1, part = 2, cap = 3, fac = 4, yoko = 5, end = 6;
            static string At(string[] r, int i) => (i >= 0 && i < r.Length) ? r[i].Trim() : "";

            // Toyota only, sorted by 終了時刻 (completion time) DESCENDING — same ordering as the day-log.
            var toyotaRows = src.Rows
                .Where(r => At(r, cust) == "トヨタ")
                .OrderByDescending(r => EndTimeKey(At(r, end)))
                .ToList();
            foreach (var r in toyotaRows)
            {
                dto.Rows.Add(new[]
                {
                    At(r, ship),                       // 出荷日
                    At(r, part),                       // 品番
                    At(r, cap),                        // 収容数(数量)
                    MapFactoryCode(At(r, fac)),        // 工場コード → A6 / A9 / ""
                    At(r, yoko),                       // ヨコオ品番
                });
            }

            _logger.LogInformation(
                "Supply export built: date={Date:yyyy-MM-dd} → {Toyota}/{Total} トヨタ rows from {Uploads} direct uploads (sorted by 終了時刻 desc).",
                date.ToDateTime(TimeOnly.MinValue), toyotaRows.Count, src.Rows.Count, raws.Count);
            return dto;
        }

        /// <summary>
        /// Remap a 工場コード to its supply code by its <b>5th–6th characters</b> (0-based index 4–5):
        /// e.g. <c>"1000T322"</c> → segment "T3" → <c>"A6"</c>; <c>"1000L324"</c> → "L3" → <c>"A9"</c>;
        /// any other value (including too-short or blank) → <c>""</c>.
        /// </summary>
        private static string MapFactoryCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 6) return "";
            var seg = code.Substring(4, 2);
            return seg == "T3" ? "A6" : seg == "L3" ? "A9" : "";
        }

        /// <summary>
        /// Apply the per-type "what to show" rules to the aggregated rows, walked in LOG-STREAM order
        /// (upload-received order then in-file row order = creation order); columns are located by their
        /// row-1 header so this survives column reordering:
        ///  • <b>monitor</b>: a 状態 = 9 (削除) row cancels the nearest 状態 = 0 (正常) row with the same
        ///    入出庫伝票番号 created BEFORE it (Android writes the delete row keeping every field, only 状態
        ///    flips → match by invoiceNo). Hide BOTH the delete row AND the original it cancels. An orphan
        ///    削除 (no prior 正常) cancels nothing — it never eats a 正常 created AFTER it.
        ///  • <b>pallet</b>: key = (PLNo., 顧客, 納入便). Keep exactly one row per key = its LATEST state:
        ///    状態 0 (積込) / 1 (移動) set the current row (a later 移動 supersedes the earlier 積込 so a
        ///    moved pallet isn't shown twice); 状態 9 (削除) clears it (removing only what was created
        ///    before). A pallet fully emptied by a 移動 (its 品目明細 is blank — buildProductDetails returns
        ///    "" once the source pallet has no invoices left) is treated as cleared too and NOT shown
        ///    (rather than a blank row). A key re-created (積込 with items) after being cleared shows again.
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
                    // 状態=9 (削除) là bản ghi "hủy" MỘT dòng 状態=0 (正常) cùng 入出庫伝票番号 ĐÃ TẠO TRƯỚC nó
                    // — Android ghi dòng xóa giữ nguyên mọi field, chỉ lật 状態 (StockViewModel.deleteSelectedStock)
                    // → ghép theo invoiceNo. Duyệt theo THỨ TỰ LOG (thứ tự nhận upload + thứ tự dòng = thứ tự
                    // tạo): mỗi 削除 hủy dòng 正常 gần nhất TRƯỚC nó của cùng invoice. 削除 "mồ côi" (không có
                    // dòng 正常 nào trước nó — vd bản gốc thuộc ngày/upload khác) KHÔNG hủy dòng nào; tuyệt đối
                    // không ăn nhầm dòng 正常 tạo SAU. Ẩn cả dòng 削除 lẫn dòng 正常 bị nó hủy. Áp cho grid + Export.
                    int st = Idx("状態"), inv = Idx("入出庫伝票番号");
                    if (st < 0) return;
                    if (inv < 0)
                    {
                        // Không định vị được khóa ghép → giữ hành vi cũ: chỉ ẩn dòng 削除.
                        dto.Rows = dto.Rows.Where(r => Cell(r, st) != "9").ToList();
                        break;
                    }
                    var keep = new bool[dto.Rows.Count];
                    for (int i = 0; i < keep.Length; i++) keep[i] = true;
                    // invoiceNo → stack chỉ số các dòng 正常 CHƯA bị hủy (đỉnh = mới nhất trước dòng đang xét).
                    var openNormals = new Dictionary<string, Stack<int>>();
                    for (int i = 0; i < dto.Rows.Count; i++)
                    {
                        var r = dto.Rows[i];
                        var s = Cell(r, st);
                        var key = Cell(r, inv);
                        if (s == "9")
                        {
                            keep[i] = false;                              // dòng xóa: luôn ẩn
                            if (openNormals.TryGetValue(key, out var stack) && stack.Count > 0)
                                keep[stack.Pop()] = false;                // hủy dòng 正常 gần nhất TẠO TRƯỚC
                        }
                        else if (s == "0")
                        {
                            if (!openNormals.TryGetValue(key, out var stack)) { stack = new Stack<int>(); openNormals[key] = stack; }
                            stack.Push(i);
                        }
                    }
                    var kept = new List<string[]>(dto.Rows.Count);
                    for (int i = 0; i < dto.Rows.Count; i++)
                        if (keep[i]) kept.Add(dto.Rows[i]);
                    dto.Rows = kept;
                    break;
                }
                case "pallet_log":
                {
                    int st = Idx("状態"), pl = Idx("PLNo."), cu = Idx("顧客"), ru = Idx("納入便");
                    if (st < 0 || pl < 0 || cu < 0 || ru < 0) return;
                    // 品目明細 nằm giữa cột (header có phần chú thích "(品目コード:箱数x数量)") → khớp theo tiền tố.
                    int detail = dto.Headers.FindIndex(h => h.Trim().StartsWith("品目明細"));
                    string KeyOf(string[] r) => $"{Cell(r, pl)}|{Cell(r, cu)}|{Cell(r, ru)}";

                    // Duyệt theo THỨ TỰ LOG (= thứ tự tạo). Mỗi khóa (PLNo.+顧客+納入便) chỉ giữ ĐÚNG 1 dòng =
                    // trạng thái MỚI NHẤT của nó:
                    //  • 状態=0 (積込) / 状態=1 (移動): dòng này thành trạng thái hiện tại → 移動 tạo sau ĐÈ 積込
                    //    tạo trước (nếu không sẽ hiện 2 dòng cho cùng 1 pallet).
                    //  • 状態=9 (削除): xóa trạng thái hiện tại — chỉ tác động cái TẠO TRƯỚC nó. Nếu cùng khóa
                    //    được TẠO LẠI sau dòng 削除 thì dòng mới lại hiện (không bị xóa oan).
                    //  • Pallet bị 移動 dời HẾT hàng: app ghi dòng 移動 với 品目明細 RỖNG (buildProductDetails
                    //    trả "" khi pallet nguồn không còn invoice). Trạng thái mới nhất rỗng ⇒ pallet đã hết
                    //    hàng → coi như đã xóa, KHÔNG hiển thị (thay vì hiện 1 dòng trống). Nếu khóa được
                    //    積込 lại sau đó (dòng mới có 品目明細) thì lại hiện bình thường.
                    var current = new Dictionary<string, int>();   // khóa → chỉ số dòng đang hiển thị (-1 = đã xóa/trống)
                    var order = new List<string>();                // thứ tự khóa xuất hiện lần đầu → hiển thị ổn định
                    for (int i = 0; i < dto.Rows.Count; i++)
                    {
                        var key = KeyOf(dto.Rows[i]);
                        if (!current.ContainsKey(key)) order.Add(key);
                        bool emptied = detail >= 0 && Cell(dto.Rows[i], detail).Length == 0;
                        current[key] = (Cell(dto.Rows[i], st) == "9" || emptied) ? -1 : i;
                    }
                    dto.Rows = order.Where(k => current[k] >= 0).Select(k => dto.Rows[current[k]]).ToList();
                    break;
                }
                // direct_log / legacy / unknown: show everything as-is.
            }
        }

        // Canonical per-type day-log DISPLAY columns — the layout the dashboard grid + Export use,
        // independent of whatever column order/count the phone actually uploaded. Each upload's rows
        // are re-projected onto these columns BY HEADER NAME (AppendCsvProjected), so the wire CSV can
        // stay 11-col for direct while the display shows the 10-col layout below (columns absent here,
        // e.g. ヨコオ品番, are dropped; 出荷日 is pulled to the front regardless of its source position).
        // Pinned so the grid + Export never drift with older/mismatched upload layouts. 状態
        // (monitor/pallet) is the trailing numeric 0/9 code. Keep in sync with the display spec image.
        //   monitor 8-col, pallet 7-col, direct 10-col.
        private static readonly string[] MonitorHeaders =
            { "開始時刻", "終了時刻", "入出庫伝票番号", "顧客コード", "品目コード", "箱数", "数量", "状態" };
        private static readonly string[] PalletHeaders =
            { "開始時刻", "終了時刻", "PLNo.", "顧客", "納入便", "品目明細 (品目コード:箱数x数量)", "状態" };
        private static readonly string[] DirectHeaders =
            { "出荷日", "開始時刻", "終了時刻", "顧客", "納入先", "工場コード", "品番", "収容数", "箱数", "納入数" };

        private static string[]? CanonicalHeaders(string typeKey) => typeKey switch
        {
            "monitor_log" => MonitorHeaders,
            "pallet_log" => PalletHeaders,
            "direct_log" => DirectHeaders,
            _ => null,
        };

        /// <summary>
        /// Parse one raw CSV and append its data rows to <paramref name="dto"/> PROJECTED onto the fixed
        /// <paramref name="canonical"/> columns — each source column is matched to a canonical column BY
        /// HEADER NAME (row 1), so reordered / extra / missing columns can't shift the data. A canonical
        /// column absent from the source becomes "". The 状態 column resolves to the LAST 状態 in the
        /// source header (the numeric 0/9 code — older logs also carried a look-alike text 状態 such as
        /// "〇 完了" as an earlier column that must NOT be mistaken for the status code).
        /// </summary>
        private static void AppendCsvProjected(CsvTableDto dto, string raw, IReadOnlyList<string> canonical)
        {
            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string>? srcHeader = null;
            var map = new int[canonical.Count];
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = CsvTypes.SplitCsv(line);
                if (srcHeader == null)
                {
                    srcHeader = fields.Select(f => f.Trim()).ToList();
                    for (int i = 0; i < canonical.Count; i++)
                    {
                        var name = canonical[i];
                        map[i] = name == "状態" ? srcHeader.LastIndexOf(name) : srcHeader.IndexOf(name);
                    }
                    continue;   // row 1 = header, not data
                }
                var row = new string[canonical.Count];
                for (int i = 0; i < canonical.Count; i++)
                {
                    int si = map[i];
                    row[i] = (si >= 0 && si < fields.Count) ? fields[si].Trim() : "";
                }
                dto.Rows.Add(row);
            }
        }

        /// <summary>
        /// Remove the named column (first match, by trimmed header) from <paramref name="dto"/> — both
        /// the header entry and the matching cell of every row. Used to strip 状態 after the display
        /// filter has consumed it, so it shows in neither the grid nor Export. No-op if absent.
        /// </summary>
        private static void RemoveColumn(CsvTableDto dto, string header)
        {
            int idx = dto.Headers.FindIndex(h => h.Trim() == header);
            if (idx < 0) return;

            dto.Headers.RemoveAt(idx);
            for (int i = 0; i < dto.Rows.Count; i++)
            {
                var r = dto.Rows[i];
                if (idx >= r.Length) continue;   // shorter row (defensive): nothing to drop at idx
                var trimmed = new string[r.Length - 1];
                Array.Copy(r, 0, trimmed, 0, idx);
                Array.Copy(r, idx + 1, trimmed, idx, r.Length - idx - 1);
                dto.Rows[i] = trimmed;
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
