using System.Data;
using LeontecSyncLogSystem.Monitoring;
using LeontecSyncLogSystem.UI;

namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Monitoring dashboard. Every 2s reads a <see cref="StatusDto"/> from the in-process
    /// <see cref="MonitorService"/>. Left-top: Bluetooth clients + server state. Left-bottom: the
    /// list of received CSV uploads. Right: the <b>full log of one day</b> for the selected CSV
    /// type — driven by a date picker (default today) + a type radio (default monitor), NOT by the
    /// CSV selected on the left. The day is taken from each upload's filename date (LogDate).
    ///
    /// All visible text is localized via <see cref="Loc"/> (English / Tiếng Việt / 日本語), switchable
    /// at runtime from the language box in the toolbar.
    /// </summary>
    // Open as CODE, never the visual WinForms Designer. InitializeComponent is hand-maintained
    // (it calls ConfigureGrid(...) and defers _split.SplitterDistance to MainForm.Load); the visual
    // designer would regenerate the method, drop those, and bake in a SplitterDistance that crashes
    // the app on startup. "Code" makes a double-click open the code editor and disables View Designer.
    [System.ComponentModel.DesignerCategory("Code")]
    public partial class MainForm : Form
    {
        // The visual controls (toolbar, panels, grids, filter bar) live in MainForm.Designer.cs
        // so the WinForms Designer can render the layout. This file holds all behaviour.
        private readonly MonitorService _monitor;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

        private bool _busy;
        private bool _langReady;       // true once the language box is populated (suppress its event during setup)
        private string _csvSig = "";
        private string _dayLogSig = "";

        // Device selection drives which device's CSVs the bottom list shows (fetched from DB).
        private string? _selectedDeviceAddr;
        private Guid? _selectedCsvId;
        private bool _suppressDeviceSel;
        private bool _suppressCsvSel;

        // Per displayed-row colour for duplicate rows (identical rows → same colour, different
        // duplicate groups → different colours). Keyed by DataTable row index.
        private readonly Dictionary<int, Color> _rowColors = new();
        private static readonly Color[] DupPalette =
        {
            Color.FromArgb(255, 236, 179), // amber
            Color.FromArgb(197, 225, 165), // green
            Color.FromArgb(179, 229, 252), // blue
            Color.FromArgb(248, 187, 208), // pink
            Color.FromArgb(225, 190, 231), // purple
            Color.FromArgb(255, 204, 188), // deep orange
            Color.FromArgb(178, 223, 219), // teal
        };

        /// <summary>
        /// Parameterless constructor for the WinForms <b>Designer only</b> — the designer must be
        /// able to instantiate the form to render it. Real instances are created from DI via
        /// <see cref="MainForm(MonitorService)"/> (see <c>Program.cs</c>). At design time the
        /// <see cref="Load"/> handler and the refresh timer never run, so the null service is never
        /// dereferenced. Do NOT use this constructor at runtime.
        /// </summary>
        public MainForm() : this(null!) { }

        public MainForm(MonitorService monitor)
        {
            InitializeComponent();   // builds the layout skeleton (MainForm.Designer.cs)
            _monitor = monitor;

            BuildColumns();
            SetupLanguageBox();
            ApplyTexts();
            Loc.Changed += OnLanguageChanged;

            // Date filter: default = today, and the user can never pick a day past today.
            _dtpDay.MaxDate = DateTime.Today;
            _dtpDay.Value = DateTime.Today;
            UpdateDateNav();

            _btnClear.Click += async (_, _) => await ClearAllAsync();
            _btnExportDay.Click += async (_, _) => await ExportCsvAsync();
            _btnPrevDay.Click += (_, _) => StepDay(-1);
            _btnNextDay.Click += (_, _) => StepDay(+1);
            _timer.Tick += async (_, _) => await RefreshAsync();
            Load += async (_, _) =>
            {
                // Min sizes + divider position are applied HERE (not in the designer): during
                // InitializeComponent the SplitContainer is still 150px wide, so setting a 420px
                // Panel2MinSize would make EndInit throw. Now the real width is known, so it's safe.
                SetupSplitter();

                _timer.Start();
                await RefreshAsync();
            };

            // The day-log grid shows whatever columns the selected type's CSVs have (their row 1).
            _dgvLogs.AutoGenerateColumns = true;

            _dgvClients.CellFormatting += FormatClientCell;
            _dgvCsv.CellFormatting += FormatCsvCell;
            _dgvLogs.CellFormatting += FormatLogCell;
            // Re-apply per-column % each time the day-log rebinds (columns are auto-generated).
            _dgvLogs.DataBindingComplete += (_, _) => ApplyLogColumnWeights();
            _dgvClients.SelectionChanged += (_, _) => OnDeviceSelectionChanged();
            _dgvCsv.SelectionChanged += (_, _) => OnCsvSelectionChanged();

            // Per-day filter changes re-query the right panel immediately.
            _dtpDay.ValueChanged += (_, _) => { UpdateDateNav(); OnDayFilterChanged(); };
            _rbMonitor.CheckedChanged += (_, _) => { if (_rbMonitor.Checked) OnDayFilterChanged(); };
            _rbPallet.CheckedChanged += (_, _) => { if (_rbPallet.Checked) OnDayFilterChanged(); };
            _rbDirect.CheckedChanged += (_, _) => { if (_rbDirect.Checked) OnDayFilterChanged(); };
        }

        private static DataGridViewTextBoxColumn Col(string prop, int fillWeight = 100) =>
            new() { DataPropertyName = prop, FillWeight = fillWeight };

        private void BuildColumns()
        {
            // Header texts are set in ApplyTexts() so they follow the active language.
            _dgvClients.Columns.AddRange(
                Col(nameof(ClientDto.Name), 100),
                Col(nameof(ClientDto.WorkerId), 80),
                Col(nameof(ClientDto.Presence), 80),
                Col(nameof(ClientDto.FramesReceived), 45),
                Col(nameof(ClientDto.RecordsIngested), 55),
                Col(nameof(ClientDto.LastHeartbeatUtc), 95),
                Col(nameof(ClientDto.LastFrameUtc), 95));

            _dgvCsv.Columns.AddRange(
                Col(nameof(ReceivedCsvDto.ReceivedAtUtc), 110),
                Col(nameof(ReceivedCsvDto.Type), 95),
                Col(nameof(ReceivedCsvDto.UploadIndex), 45),
                Col(nameof(ReceivedCsvDto.RowCount), 50));

            // _dgvLogs columns are generated dynamically per selected type/day (headers = its row 1).
        }

        // ---------------- Localization ----------------

        private void SetupLanguageBox()
        {
            // Order MUST match the AppLang enum (En=0, Vi=1, Ja=2) so the index maps directly.
            _cmbLang.Items.AddRange(new object[] { "English", "Tiếng Việt", "日本語" });
            _cmbLang.SelectedIndex = (int)Loc.Current;
            _langReady = true;
            _cmbLang.SelectedIndexChanged += (_, _) =>
            {
                if (_langReady && _cmbLang.SelectedIndex >= 0)
                    Loc.SetLanguage((AppLang)_cmbLang.SelectedIndex);
            };
        }

        private void OnLanguageChanged()
        {
            ApplyTexts();
            _dayLogSig = "";   // force the day-log header to re-render in the new language
            _ = RefreshAsync();
        }

        /// <summary>Re-applies every static caption/header from <see cref="Loc"/>.</summary>
        private void ApplyTexts()
        {
            _btnClear.Text = Loc.T("btn_clear");
            _btnExportDay.Text = Loc.T("btn_export");

            _grpClients.Text = Loc.T("grp_clients");
            _grpCsv.Text = Loc.T("grp_csv");
            _grpLogs.Text = Loc.T("grp_daylog");

            _lblDate.Text = Loc.T("lbl_date");
            _lblTypeFilter.Text = Loc.T("lbl_type");
            _rbMonitor.Text = TypeLabel("monitor_log");
            _rbPallet.Text = TypeLabel("pallet_log");
            _rbDirect.Text = TypeLabel("direct_log");

            foreach (DataGridViewColumn c in _dgvClients.Columns)
                c.HeaderText = ClientHeader(c.DataPropertyName);
            foreach (DataGridViewColumn c in _dgvCsv.Columns)
                c.HeaderText = CsvHeader(c.DataPropertyName);
        }

        private static string ClientHeader(string prop) => prop switch
        {
            nameof(ClientDto.Name) => Loc.T("col_name"),
            nameof(ClientDto.WorkerId) => Loc.T("col_device"),
            nameof(ClientDto.Presence) => Loc.T("col_presence"),
            nameof(ClientDto.FramesReceived) => Loc.T("col_frames"),
            nameof(ClientDto.RecordsIngested) => Loc.T("col_records"),
            nameof(ClientDto.LastHeartbeatUtc) => Loc.T("col_last_hb"),
            nameof(ClientDto.LastFrameUtc) => Loc.T("col_last_data"),
            _ => prop,
        };

        private static string CsvHeader(string prop) => prop switch
        {
            nameof(ReceivedCsvDto.ReceivedAtUtc) => Loc.T("col_time"),
            nameof(ReceivedCsvDto.Type) => Loc.T("col_type"),
            nameof(ReceivedCsvDto.UploadIndex) => Loc.T("col_index"),
            nameof(ReceivedCsvDto.RowCount) => Loc.T("col_rows"),
            _ => prop,
        };

        /// <summary>The CSV type currently selected by the right-panel radios.</summary>
        private string SelectedTypeKey =>
            _rbPallet.Checked ? "pallet_log" : _rbDirect.Checked ? "direct_log" : "monitor_log";

        // ---------------- Refresh ----------------

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var status = await _monitor.GetSnapshotAsync();

                _lblConn.Text = Loc.T("conn_running");
                _lblConn.ForeColor = Color.ForestGreen;
                _lblUptime.Text = Loc.T("uptime", FormatUptime(status.UptimeSeconds));
                _lblTotals.Text = Loc.T("totals", status.Logs.Today.ToString("N0"), status.Logs.Total.ToString("N0"));

                var bt = status.BtServer;
                if (bt.Listening)
                {
                    var radio = string.IsNullOrWhiteSpace(bt.RadioName) ? Loc.T("unknown") : bt.RadioName;
                    _lblServer.Text = Loc.T("bt_listening", radio);
                    _lblServer.ForeColor = Color.ForestGreen;
                }
                else
                {
                    _lblServer.Text = Loc.T("bt_not_listening", bt.LastError ?? Loc.T("bt_initializing"));
                    _lblServer.ForeColor = Color.Firebrick;
                }

                UpdateClients(status.Clients);
                await RefreshCsvListAsync();
                await RefreshDayLogAsync();
            }
            catch (Exception ex)
            {
                _lblConn.Text = Loc.T("conn_error", ex.Message);
                _lblConn.ForeColor = Color.Firebrick;
                _lblUptime.Text = "";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task ClearAllAsync()
        {
            var answer = MessageBox.Show(
                Loc.T("clear_confirm_body"),
                Loc.T("clear_confirm_title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            try
            {
                int deleted = await _monitor.ClearAllAsync();
                _selectedCsvId = null;
                _csvSig = "";           // force the CSV grid to rebind (now empty)
                _dayLogSig = "";        // force the day-log to rebind (now empty)
                _dgvLogs.DataSource = null;
                await RefreshAsync();
                MessageBox.Show(Loc.T("clear_done", deleted.ToString("N0")),
                    Loc.T("done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("clear_error", ex.Message), Loc.T("error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Export the day-log to a CSV file — <b>exactly</b> what the grid currently shows. The grid
        /// is bound to a <see cref="DataTable"/> built once in <see cref="RefreshDayLogAsync"/>
        /// (same type + date, same per-type display filter, same "#" ordinal column, same row order);
        /// we serialize that very table so display and export can never drift. Change how the table is
        /// built in one place and both the grid and this export follow. UTF-8 with BOM so Excel shows
        /// Japanese.
        /// </summary>
        private async Task ExportCsvAsync()
        {
            if (_dgvLogs.DataSource is not DataTable dt || dt.Rows.Count == 0)
            {
                MessageBox.Show(Loc.T("export_empty"), Loc.T("done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var typeKey = SelectedTypeKey;
            var date = DateOnly.FromDateTime(_dtpDay.Value.Date);

            using var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"{typeKey}_{date:yyyyMMdd}.csv",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                // Header = the grid's columns (includes the "#" ordinal); rows = the grid's rows.
                sb.Append(string.Join(",", dt.Columns.Cast<DataColumn>().Select(c => CsvEscape(c.ColumnName)))).Append("\r\n");
                foreach (DataRow r in dt.Rows)
                    sb.Append(string.Join(",", r.ItemArray.Select(o => CsvEscape(o?.ToString() ?? "")))).Append("\r\n");

                await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(Loc.T("export_done", dt.Rows.Count.ToString("N0"), dlg.FileName),
                    Loc.T("done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("export_error", ex.Message), Loc.T("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string CsvEscape(string s)
        {
            s ??= "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>Rebind the clients grid, keeping the selected device (default: first).</summary>
        private void UpdateClients(List<ClientDto> clients)
        {
            _suppressDeviceSel = true;
            _dgvClients.DataSource = clients;

            int target = 0;
            if (_selectedDeviceAddr is not null)
            {
                var idx = clients.FindIndex(c => c.Address == _selectedDeviceAddr);
                if (idx >= 0) target = idx;
            }

            if (clients.Count > 0 && target < _dgvClients.Rows.Count)
            {
                _dgvClients.ClearSelection();
                _dgvClients.Rows[target].Selected = true;
                _dgvClients.CurrentCell = _dgvClients.Rows[target].Cells[0];
                _selectedDeviceAddr = clients[target].Address;
            }
            else if (clients.Count == 0)
            {
                _selectedDeviceAddr = null;
            }
            _suppressDeviceSel = false;
        }

        private void OnDeviceSelectionChanged()
        {
            if (_suppressDeviceSel) return;
            _selectedDeviceAddr = (_dgvClients.CurrentRow?.DataBoundItem as ClientDto)?.Address;
            _ = RefreshCsvListAsync();
        }

        /// <summary>Fetch the selected device's CSV uploads from the DB and show them.</summary>
        private async Task RefreshCsvListAsync()
        {
            try
            {
                // Filter the list by the selected day (by the CSV filename date / LogDate), so the
                // bottom-left list shows only that day's uploads — in step with the date filter.
                var day = DateOnly.FromDateTime(_dtpDay.Value.Date);
                var csvs = await _monitor.GetCsvsForDeviceAsync(_selectedDeviceAddr, day);
                UpdateCsvList(csvs);
            }
            catch
            {
                // Non-fatal: the day-log panel and totals are unaffected by a CSV-list hiccup.
            }
        }

        /// <summary>Rebind the CSV list only when it actually changed, so selection is kept.</summary>
        private void UpdateCsvList(List<ReceivedCsvDto> csvs)
        {
            var sig = $"{_selectedDeviceAddr}|{_dtpDay.Value.Date:yyyyMMdd}|{csvs.Count}:{(csvs.Count > 0 ? csvs[0].Id.ToString() : "-")}";
            if (sig == _csvSig)
                return;
            _csvSig = sig;

            _suppressCsvSel = true;
            _dgvCsv.DataSource = csvs;

            // Restore the previously selected CSV; otherwise select the newest (top).
            int target = 0;
            if (_selectedCsvId is Guid id)
            {
                var idx = csvs.FindIndex(c => c.Id == id);
                if (idx >= 0) target = idx;
            }

            if (csvs.Count > 0 && target < _dgvCsv.Rows.Count)
            {
                _dgvCsv.ClearSelection();
                _dgvCsv.Rows[target].Selected = true;
                _dgvCsv.CurrentCell = _dgvCsv.Rows[target].Cells[0];
            }
            _suppressCsvSel = false;
        }

        // The CSV list is now informational only — its selection no longer drives the right panel
        // (the right panel is filtered by date + type instead). We just remember the selection.
        private void OnCsvSelectionChanged()
        {
            if (_suppressCsvSel) return;
            _selectedCsvId = (_dgvCsv.CurrentRow?.DataBoundItem as ReceivedCsvDto)?.Id;
        }

        private void OnDayFilterChanged()
        {
            _dayLogSig = "";   // force a rebind even if the row count happens to match
            _csvSig = "";      // the bottom-left CSV list is date-filtered too → force it to rebind
            _ = RefreshDayLogAsync();
            _ = RefreshCsvListAsync();
        }

        /// <summary>
        /// Apply the splitter's panel min-sizes and initial divider position (~30% left), clamped to
        /// the real width. Done at runtime — never in InitializeComponent — because a 420px
        /// Panel2MinSize set while the control is still 150px wide makes EndInit() throw
        /// "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize".
        /// </summary>
        private void SetupSplitter()
        {
            int w = _split.Width;
            if (w <= 0) return;   // not laid out yet; the designer defaults stay until it is

            // Preferred min sizes, but never let (Panel1Min + Panel2Min + splitter) exceed the width.
            int p1Min = Math.Min(260, w / 4);
            int p2Min = Math.Min(420, w / 2);
            if (p1Min + p2Min + _split.SplitterWidth >= w)
            {
                int third = Math.Max(0, (w - _split.SplitterWidth) / 3);
                p1Min = third;
                p2Min = third;
            }
            _split.Panel1MinSize = p1Min;
            _split.Panel2MinSize = p2Min;

            int max = w - p2Min - _split.SplitterWidth;
            if (max > p1Min)
                _split.SplitterDistance = Math.Clamp((int)(w * 0.30), p1Min, max);
        }

        /// <summary>Step the date filter by <paramref name="delta"/> days, clamped to [MinDate, today].</summary>
        private void StepDay(int delta)
        {
            var target = _dtpDay.Value.Date.AddDays(delta);
            if (target < _dtpDay.MinDate.Date) target = _dtpDay.MinDate.Date;
            if (target > _dtpDay.MaxDate.Date) target = _dtpDay.MaxDate.Date;
            if (target != _dtpDay.Value.Date)
                _dtpDay.Value = target;   // fires ValueChanged → UpdateDateNav + OnDayFilterChanged
        }

        /// <summary>Enable/disable the ‹ › buttons so the user can't step before MinDate or past today.</summary>
        private void UpdateDateNav()
        {
            _btnPrevDay.Enabled = _dtpDay.Value.Date > _dtpDay.MinDate.Date;
            _btnNextDay.Enabled = _dtpDay.Value.Date < _dtpDay.MaxDate.Date;
        }

        /// <summary>
        /// Fetch the full day-log for the selected type + date (aggregated across all uploads of
        /// that day) and show it with dynamic columns; colour identical rows (same data → same
        /// colour, each dup group its own colour). Skips rebinding when nothing changed.
        /// </summary>
        private async Task RefreshDayLogAsync()
        {
            var typeKey = SelectedTypeKey;
            var date = DateOnly.FromDateTime(_dtpDay.Value.Date);

            CsvTableDto table;
            try { table = await _monitor.GetDayLogAsync(typeKey, date); }
            catch (Exception ex) { _grpLogs.Text = Loc.T("err_load_daylog", ex.Message); return; }

            // Cheap signature: type + day + size. Aggregation is append-only per (type, day), so a
            // changed row count is enough to detect new data without re-rendering every 2s.
            var sig = $"{typeKey}|{date:yyyyMMdd}|{table.Rows.Count}|{table.Headers.Count}";
            if (sig == _dayLogSig)
                return;
            _dayLogSig = sig;
            _rowColors.Clear();

            // Build a DataTable: "#" ordinal + the type's own headers (row 1).
            var dt = new DataTable();
            dt.Columns.Add("#");
            var used = new HashSet<string>(StringComparer.Ordinal) { "#" };
            foreach (var h in table.Headers)
            {
                var name = string.IsNullOrWhiteSpace(h) ? "(col)" : h;
                var unique = name;
                int k = 2;
                while (!used.Add(unique)) unique = $"{name} ({k++})";
                dt.Columns.Add(unique);
            }

            int colCount = table.Headers.Count;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var src = table.Rows[i];
                var cells = new object[colCount + 1];
                cells[0] = (i + 1).ToString();
                for (int c = 0; c < colCount; c++)
                    cells[c + 1] = c < src.Length ? src[c] : "";
                dt.Rows.Add(cells);
            }

            // Duplicate detection: identical full data row → same colour; each group a new colour.
            var sigToRows = new Dictionary<string, List<int>>();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var rowSig = string.Join("", table.Rows[i]);
                if (!sigToRows.TryGetValue(rowSig, out var list)) { list = new List<int>(); sigToRows[rowSig] = list; }
                list.Add(i);
            }
            int g = 0;
            foreach (var kv in sigToRows)
            {
                if (kv.Value.Count < 2) continue;
                var color = DupPalette[g++ % DupPalette.Length];
                foreach (var ri in kv.Value) _rowColors[ri] = color;
            }

            _dgvLogs.DataSource = dt;

            var dupRows = _rowColors.Count;
            _grpLogs.Text =
                Loc.T("daylog_header", TypeLabel(typeKey), date.ToString("yyyy-MM-dd"), table.Rows.Count) +
                (dupRows > 0 ? Loc.T("daylog_dups", dupRows) : "");
        }

        /// <summary>
        /// Give each auto-generated day-log column a proportional <see cref="DataGridViewColumn.FillWeight"/>
        /// (relative %), so Fill mode stretches them to the right edge with sensible per-column widths
        /// (wide for 品目明細, narrow for #/状態). Header text = the CSV's own column name.
        /// </summary>
        private void ApplyLogColumnWeights()
        {
            foreach (DataGridViewColumn c in _dgvLogs.Columns)
                c.FillWeight = LogColWeight(c.HeaderText);
        }

        private static int LogColWeight(string header)
        {
            var h = (header ?? "").Trim();
            if (h == "#") return 20;
            if (h.Contains("品目明細")) return 220;   // longest field (code:boxesxqty …)
            return h switch
            {
                "開始時刻" or "終了時刻" => 75,
                "入出庫伝票番号" => 95,
                "顧客コード" => 80,
                "顧客" => 70,
                "品目コード" => 80,
                "品番" => 85,
                "箱数" or "数量" or "収容数" or "納入数" or "積込箱数" => 45,
                "状態" => 45,
                "PLNo." => 45,
                "納入便" => 70,
                "納入先" => 75,
                "出荷日" => 75,
                "工場コード" => 65,
                "ヨコオ品番" => 95,
                _ => 80,
            };
        }

        private static string TypeLabel(string type) => type switch
        {
            "monitor_log" => Loc.T("type_monitor"),
            "pallet_log" => Loc.T("type_pallet"),
            "direct_log" => Loc.T("type_direct"),
            "legacy" => Loc.T("type_legacy"),
            _ => Loc.T("type_other"),
        };

        private static string FormatUptime(long seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalDays >= 1
                ? $"{(int)t.TotalDays}d {t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        }

        private void FormatClientCell(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = (DataGridView)sender!;
            var colName = grid.Columns[e.ColumnIndex].DataPropertyName;

            if (colName == nameof(ClientDto.Presence) && e.Value is string presence)
            {
                (e.CellStyle!.BackColor, e.CellStyle.ForeColor, e.Value) = presence == "Online"
                    ? (Color.FromArgb(214, 245, 214), Color.DarkGreen, (object)Loc.T("online"))
                    : (Color.FromArgb(238, 238, 238), Color.Gray, Loc.T("offline"));
                e.FormattingApplied = true;
            }
            else
            {
                FormatDateCells(sender, e);
            }
        }

        private void FormatLogCell(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            // Tint duplicate rows: light dup colour + dark text when NOT selected. Leave the
            // selection colours at the grid default (blue + white) so a clicked row clearly shows
            // focus and stays readable — overriding SelectionBackColor before made selecting a dup
            // look identical to not selecting it.
            if (e.RowIndex >= 0 && _rowColors.TryGetValue(e.RowIndex, out var color))
            {
                e.CellStyle!.BackColor = color;
                e.CellStyle.ForeColor = Color.Black;
            }
        }

        private void FormatCsvCell(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = (DataGridView)sender!;
            var colName = grid.Columns[e.ColumnIndex].DataPropertyName;

            if (colName == nameof(ReceivedCsvDto.Type) && e.Value is string t)
            {
                e.Value = TypeLabel(t);
                e.FormattingApplied = true;
            }

            FormatDateCells(sender, e);

            // Gray out superseded uploads (older index of the same terminal+type).
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is ReceivedCsvDto d && d.Superseded)
                e.CellStyle!.ForeColor = Color.Silver;
        }

        private void FormatDateCells(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = (DataGridView)sender!;
            var colName = grid.Columns[e.ColumnIndex].DataPropertyName;
            if (e.Value is DateTime dt &&
                colName is nameof(ClientDto.LastFrameUtc) or nameof(ClientDto.LastHeartbeatUtc)
                        or nameof(ReceivedCsvDto.ReceivedAtUtc))
            {
                var local = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                e.Value = local.ToString("MM-dd HH:mm:ss");
                e.FormattingApplied = true;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            Loc.Changed -= OnLanguageChanged;
            base.OnFormClosed(e);
        }
    }
}
