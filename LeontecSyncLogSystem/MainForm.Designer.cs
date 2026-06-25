namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Designer-generated half of <see cref="MainForm"/>. Holds the <b>static layout skeleton</b>
    /// (toolbar, the two left panels, and the right day-log panel) so the WinForms Designer can
    /// render and edit the structure. Everything dynamic — the <see cref="Monitoring.MonitorService"/>
    /// passed in by DI, grid columns, localized text, data binding and event wiring — stays in
    /// <c>MainForm.cs</c>. The texts set here are design-time placeholders; at runtime
    /// <c>ApplyTexts()</c> overwrites them from the active language.
    ///
    /// <para><b>⚠️ Hand-maintained — do NOT open this form in the visual WinForms Designer.</b>
    /// The designer regenerates <c>InitializeComponent</c> in its verbose form and, in doing so,
    /// (1) drops the <c>ConfigureGrid(...)</c> helper calls (grids lose ReadOnly / no-auto-columns)
    /// and (2) bakes in a fixed <c>_split.SplitterDistance</c>. That baked-in distance throws
    /// <see cref="System.InvalidOperationException"/> inside <c>InitializeComponent</c> when the form
    /// realizes at a different width/DPI than design time → the app crashes on startup before the
    /// window even shows. The initial splitter position is therefore set at runtime in
    /// <c>MainForm.Load</c>, never here.</para>
    /// </summary>
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        private FlowLayoutPanel _bar;
        private Button _btnClear;   // labelled "Reset" — clears all data via ClearAllAsync
        private Label _lblConn;
        private Label _lblUptime;
        private Label _lblTotals;
        private ComboBox _cmbLang;

        private TableLayoutPanel _root;
        private SplitContainer _split;        // draggable left/right divider (resizable panels)
        private TableLayoutPanel _leftLayout;

        private GroupBox _grpClients;
        private TableLayoutPanel _clientsLayout;
        private Label _lblServer;
        private DataGridView _dgvClients;

        private GroupBox _grpCsv;
        private DataGridView _dgvCsv;

        private GroupBox _grpLogs;
        private TableLayoutPanel _logsLayout;
        private Panel _filterRow;             // filter bar (left) + Export button (right)
        private FlowLayoutPanel _filterBar;
        private Label _lblDate;
        private Button _btnPrevDay;           // ‹ : go to the previous day
        private DateTimePicker _dtpDay;
        private Button _btnNextDay;           // › : go to the next day (disabled at today)
        private Label _lblTypeFilter;
        private RadioButton _rbMonitor;
        private RadioButton _rbPallet;
        private RadioButton _rbDirect;
        private Button _btnExportDay;         // CSV出力 — exports exactly what the grid shows
        private DataGridView _dgvLogs;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            _bar = new FlowLayoutPanel();
            _btnClear = new Button();
            _lblConn = new Label();
            _lblUptime = new Label();
            _lblTotals = new Label();
            _cmbLang = new ComboBox();
            _root = new TableLayoutPanel();
            _split = new SplitContainer();
            _leftLayout = new TableLayoutPanel();
            _grpClients = new GroupBox();
            _clientsLayout = new TableLayoutPanel();
            _lblServer = new Label();
            _dgvClients = new DataGridView();
            _grpCsv = new GroupBox();
            _dgvCsv = new DataGridView();
            _grpLogs = new GroupBox();
            _logsLayout = new TableLayoutPanel();
            _filterRow = new Panel();
            _filterBar = new FlowLayoutPanel();
            _lblDate = new Label();
            _btnPrevDay = new Button();
            _dtpDay = new DateTimePicker();
            _btnNextDay = new Button();
            _lblTypeFilter = new Label();
            _rbMonitor = new RadioButton();
            _rbPallet = new RadioButton();
            _rbDirect = new RadioButton();
            _btnExportDay = new Button();
            _dgvLogs = new DataGridView();

            _bar.SuspendLayout();
            _root.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_split).BeginInit();
            _split.Panel1.SuspendLayout();
            _split.Panel2.SuspendLayout();
            _split.SuspendLayout();
            _leftLayout.SuspendLayout();
            _grpClients.SuspendLayout();
            _clientsLayout.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvClients).BeginInit();
            _grpCsv.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvCsv).BeginInit();
            _grpLogs.SuspendLayout();
            _logsLayout.SuspendLayout();
            _filterRow.SuspendLayout();
            _filterBar.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvLogs).BeginInit();
            SuspendLayout();

            // _root — 1 col, 2 rows: toolbar (44px) on top, body fills the rest.
            _root.ColumnCount = 1;
            _root.RowCount = 2;
            _root.Dock = DockStyle.Fill;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _root.Controls.Add(_bar, 0, 0);
            _root.Controls.Add(_split, 0, 1);

            // _bar — toolbar.
            _bar.Dock = DockStyle.Fill;
            _bar.WrapContents = false;
            _bar.Padding = new Padding(6, 8, 0, 0);
            _bar.Controls.Add(_btnClear);
            _bar.Controls.Add(_lblConn);
            _bar.Controls.Add(_lblUptime);
            _bar.Controls.Add(_lblTotals);
            _bar.Controls.Add(_cmbLang);

            _btnClear.AutoSize = true;
            _btnClear.ForeColor = Color.Firebrick;
            _btnClear.Text = "Reset";

            _lblConn.AutoSize = true;
            _lblConn.ForeColor = Color.DarkGoldenrod;
            _lblConn.Padding = new Padding(8, 6, 0, 0);
            _lblConn.Text = "● Starting…";

            _lblUptime.AutoSize = true;
            _lblUptime.ForeColor = Color.DimGray;
            _lblUptime.Padding = new Padding(12, 6, 0, 0);

            _lblTotals.AutoSize = true;
            _lblTotals.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _lblTotals.Padding = new Padding(12, 6, 0, 0);

            _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLang.Margin = new Padding(16, 8, 0, 0);
            _cmbLang.Width = 120;

            // _split — vertical splitter so the user can drag the left/right boundary. Panel1 =
            // the left column (clients + received CSVs), Panel2 = the right day-log panel.
            // IMPORTANT: Panel1MinSize / Panel2MinSize / SplitterDistance are ALL set at runtime in
            // MainForm.Load, NOT here. During InitializeComponent the control is still at its default
            // 150px width, so EndInit()'s ApplyPanel*MinSize would compute (150 - 420) < 0 and throw
            // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize". Leaving the
            // min sizes at their defaults (25) keeps EndInit valid; we widen them once laid out.
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            _split.SplitterWidth = 6;
            _split.Panel1.Controls.Add(_leftLayout);
            _split.Panel2.Controls.Add(_grpLogs);

            // _leftLayout — clients group (230px) over the CSV-uploads group.
            _leftLayout.ColumnCount = 1;
            _leftLayout.RowCount = 2;
            _leftLayout.Dock = DockStyle.Fill;
            _leftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230F));
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _leftLayout.Controls.Add(_grpClients, 0, 0);
            _leftLayout.Controls.Add(_grpCsv, 0, 1);

            // _grpClients — server-state label (top) + the clients grid.
            _grpClients.Dock = DockStyle.Fill;
            _grpClients.Padding = new Padding(6);
            _grpClients.Text = "Bluetooth devices";
            _grpClients.Controls.Add(_clientsLayout);

            _clientsLayout.ColumnCount = 1;
            _clientsLayout.RowCount = 2;
            _clientsLayout.Dock = DockStyle.Fill;
            _clientsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _clientsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            _clientsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _clientsLayout.Controls.Add(_lblServer, 0, 0);
            _clientsLayout.Controls.Add(_dgvClients, 0, 1);

            _lblServer.Dock = DockStyle.Fill;
            _lblServer.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _lblServer.Padding = new Padding(6, 0, 0, 0);
            _lblServer.TextAlign = ContentAlignment.MiddleLeft;
            _lblServer.Text = "● Bluetooth SPP";

            ConfigureGrid(_dgvClients);

            // _grpCsv — the received-CSV list.
            _grpCsv.Dock = DockStyle.Fill;
            _grpCsv.Padding = new Padding(6);
            _grpCsv.Text = "Received CSVs";
            _grpCsv.Controls.Add(_dgvCsv);

            ConfigureGrid(_dgvCsv);

            // _grpLogs — the per-day log (filter bar + grid).
            _grpLogs.Dock = DockStyle.Fill;
            _grpLogs.Padding = new Padding(6);
            _grpLogs.Text = "Day log";
            _grpLogs.Controls.Add(_logsLayout);

            _logsLayout.ColumnCount = 1;
            _logsLayout.RowCount = 2;
            _logsLayout.Dock = DockStyle.Fill;
            _logsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _logsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            _logsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _logsLayout.Controls.Add(_filterRow, 0, 0);
            _logsLayout.Controls.Add(_dgvLogs, 0, 1);

            // _filterRow — host for the filter bar (docked left) and the Export button (docked
            // right). Add Fill first, then Right, so the flow bar takes the leftover width.
            _filterRow.Dock = DockStyle.Fill;
            _filterRow.Controls.Add(_filterBar);
            _filterRow.Controls.Add(_btnExportDay);

            // _filterBar — date label + ‹ prev / picker / next › + type radios.
            _filterBar.Dock = DockStyle.Fill;
            _filterBar.WrapContents = false;
            _filterBar.Controls.Add(_lblDate);
            _filterBar.Controls.Add(_btnPrevDay);
            _filterBar.Controls.Add(_dtpDay);
            _filterBar.Controls.Add(_btnNextDay);
            _filterBar.Controls.Add(_lblTypeFilter);
            _filterBar.Controls.Add(_rbMonitor);
            _filterBar.Controls.Add(_rbPallet);
            _filterBar.Controls.Add(_rbDirect);

            _lblDate.AutoSize = true;
            _lblDate.Padding = new Padding(4, 8, 4, 0);
            _lblDate.Text = "Date:";

            _btnPrevDay.AutoSize = false;
            _btnPrevDay.Margin = new Padding(0, 4, 0, 0);
            _btnPrevDay.Size = new Size(28, 26);
            _btnPrevDay.Text = "‹";

            _dtpDay.Format = DateTimePickerFormat.Short;
            _dtpDay.Margin = new Padding(2, 4, 2, 0);
            _dtpDay.Width = 120;

            _btnNextDay.AutoSize = false;
            _btnNextDay.Margin = new Padding(0, 4, 0, 0);
            _btnNextDay.Size = new Size(28, 26);
            _btnNextDay.Text = "›";

            _lblTypeFilter.AutoSize = true;
            _lblTypeFilter.Padding = new Padding(16, 8, 4, 0);
            _lblTypeFilter.Text = "Type:";

            _rbMonitor.AutoSize = true;
            _rbMonitor.Checked = true;
            _rbMonitor.Padding = new Padding(8, 6, 0, 0);
            _rbMonitor.Text = "Monitor";

            _rbPallet.AutoSize = true;
            _rbPallet.Padding = new Padding(8, 6, 0, 0);
            _rbPallet.Text = "Pallet";

            _rbDirect.AutoSize = true;
            _rbDirect.Padding = new Padding(8, 6, 0, 0);
            _rbDirect.Text = "Direct";

            // _btnExportDay — right of the filter; exports exactly the rows/columns shown below.
            _btnExportDay.Dock = DockStyle.Right;
            _btnExportDay.AutoSize = true;
            _btnExportDay.Padding = new Padding(8, 0, 8, 0);
            _btnExportDay.Margin = new Padding(0, 4, 0, 4);
            _btnExportDay.Text = "Export CSV";

            ConfigureGrid(_dgvLogs);
            _dgvLogs.AutoGenerateColumns = true;
            // Fill mode: columns stretch to the right edge of the window. Per-column proportions
            // (FillWeight) are assigned by header in MainForm.ApplyLogColumnWeights so wide columns
            // (品目明細) get more % and short ones (#, 状態) less.
            _dgvLogs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            // MainForm
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9F);
            // Wider default window (~1.5× the old 1084px width) for the day-log table.
            ClientSize = new Size(1626, 760);
            MinimumSize = new Size(880, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Leontec Sync Monitor";
            Controls.Add(_root);

            _bar.ResumeLayout(false);
            _bar.PerformLayout();
            _root.ResumeLayout(false);
            _split.Panel1.ResumeLayout(false);
            _split.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_split).EndInit();
            _split.ResumeLayout(false);
            _leftLayout.ResumeLayout(false);
            _grpClients.ResumeLayout(false);
            _clientsLayout.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_dgvClients).EndInit();
            _grpCsv.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_dgvCsv).EndInit();
            _grpLogs.ResumeLayout(false);
            _logsLayout.ResumeLayout(false);
            _filterRow.ResumeLayout(false);
            _filterBar.ResumeLayout(false);
            _filterBar.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvLogs).EndInit();
            ResumeLayout(false);
        }

        /// <summary>Shared read-only, fill-docked grid style (was the old <c>MakeGrid()</c> factory).</summary>
        private static void ConfigureGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }
    }
}
