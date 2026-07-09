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

        private TableLayoutPanel _bar;
        private Button _btnClear;   // labelled "Reset" — clears all data via ClearAllAsync
        private Button _btnOpenBackup;  // opens the on-disk CSV backup folder in Explorer
        private Label _lblConn;
        private Label _lblUptime;
        private Label _lblTotals;
        private ComboBox _cmbLang;

        private TableLayoutPanel _root;
        private SplitContainer _split;        // draggable left/right divider (resizable panels)
        private TableLayoutPanel _leftLayout;

        // Master toolbar (above the devices list): edit each master + push to phones.
        private TableLayoutPanel _masterBar;
        private Button _btnMasterCustomer;    // open 顧客マスタ in the right panel (editable)
        private Button _btnMasterItem;        // open 品目マスタ in the right panel (editable)
        private Button _btnSyncMaster;        // arm the current masters for the next phone sync

        private GroupBox _grpClients;
        private TableLayoutPanel _clientsLayout;
        private Label _lblServer;
        private DataGridView _dgvClients;

        private GroupBox _grpCsv;
        private DataGridView _dgvCsv;

        private GroupBox _grpLogs;
        private TableLayoutPanel _logsLayout;
        private TableLayoutPanel _filterRow;  // single-row grid: every control anchored + vertically centred
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

        // Master edit panel (right side; shown instead of the day log when a master is open).
        private GroupBox _grpMaster;
        private TableLayoutPanel _masterLayout;
        private TableLayoutPanel _masterEditRow;  // single-row toolbar: title + add/delete/save/close
        private Label _lblMasterTitle;
        private Button _btnMasterAdd;             // append a blank row
        private Button _btnMasterDelete;          // delete the selected row(s)
        private Button _btnMasterSave;            // write the grid back to the master CSV
        private Button _btnMasterClose;           // return to the day-log view
        private DataGridView _dgvMaster;          // EDITABLE grid bound to the master DataTable

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            _bar = new TableLayoutPanel();
            _btnClear = new Button();
            _btnOpenBackup = new Button();
            _lblConn = new Label();
            _lblUptime = new Label();
            _lblTotals = new Label();
            _cmbLang = new ComboBox();
            _root = new TableLayoutPanel();
            _split = new SplitContainer();
            _leftLayout = new TableLayoutPanel();
            _masterBar = new TableLayoutPanel();
            _btnMasterCustomer = new Button();
            _btnMasterItem = new Button();
            _btnSyncMaster = new Button();
            _grpClients = new GroupBox();
            _clientsLayout = new TableLayoutPanel();
            _lblServer = new Label();
            _dgvClients = new DataGridView();
            _grpCsv = new GroupBox();
            _dgvCsv = new DataGridView();
            _grpLogs = new GroupBox();
            _logsLayout = new TableLayoutPanel();
            _filterRow = new TableLayoutPanel();
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
            _grpMaster = new GroupBox();
            _masterLayout = new TableLayoutPanel();
            _masterEditRow = new TableLayoutPanel();
            _lblMasterTitle = new Label();
            _btnMasterAdd = new Button();
            _btnMasterDelete = new Button();
            _btnMasterSave = new Button();
            _btnMasterClose = new Button();
            _dgvMaster = new DataGridView();

            _bar.SuspendLayout();
            _root.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_split).BeginInit();
            _split.Panel1.SuspendLayout();
            _split.Panel2.SuspendLayout();
            _split.SuspendLayout();
            _leftLayout.SuspendLayout();
            _masterBar.SuspendLayout();
            _grpClients.SuspendLayout();
            _clientsLayout.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvClients).BeginInit();
            _grpCsv.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvCsv).BeginInit();
            _grpLogs.SuspendLayout();
            _logsLayout.SuspendLayout();
            _filterRow.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvLogs).BeginInit();
            _grpMaster.SuspendLayout();
            _masterLayout.SuspendLayout();
            _masterEditRow.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvMaster).BeginInit();
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

            // _bar — toolbar. A single-row TableLayoutPanel (was a FlowLayoutPanel with per-control
            // top-padding hacks that left the buttons, labels and the language combo on different
            // baselines). Every control is AutoSize'd and anchored so the grid centres it vertically:
            // buttons, status labels and the combo now line up on ONE baseline. Spacing = per-control
            // left Margin only (no Padding offsets). Column 5 is a flexible gap that pushes the
            // language combo to the right edge.
            _bar.Dock = DockStyle.Fill;
            _bar.Margin = new Padding(0);
            _bar.Padding = new Padding(4, 0, 4, 0);
            _bar.ColumnCount = 7;
            _bar.RowCount = 1;
            _bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 0 Reset
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 1 Open backup
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 2 Conn
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 3 Uptime
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 4 Totals
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // 5 flexible gap
            _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 6 Language combo
            _bar.Controls.Add(_btnClear, 0, 0);
            _bar.Controls.Add(_btnOpenBackup, 1, 0);
            _bar.Controls.Add(_lblConn, 2, 0);
            _bar.Controls.Add(_lblUptime, 3, 0);
            _bar.Controls.Add(_lblTotals, 4, 0);
            _bar.Controls.Add(_cmbLang, 6, 0);

            _btnClear.Anchor = AnchorStyles.Left;
            _btnClear.AutoSize = true;
            _btnClear.Margin = new Padding(2, 0, 0, 0);
            _btnClear.ForeColor = Color.Firebrick;
            _btnClear.Text = "Reset";

            _btnOpenBackup.Anchor = AnchorStyles.Left;
            _btnOpenBackup.AutoSize = true;
            _btnOpenBackup.Margin = new Padding(8, 0, 0, 0);
            _btnOpenBackup.Text = "Open backup folder";

            _lblConn.Anchor = AnchorStyles.Left;
            _lblConn.AutoSize = true;
            _lblConn.Margin = new Padding(14, 0, 0, 0);
            _lblConn.ForeColor = Color.DarkGoldenrod;
            _lblConn.Text = "● Starting…";

            _lblUptime.Anchor = AnchorStyles.Left;
            _lblUptime.AutoSize = true;
            _lblUptime.Margin = new Padding(14, 0, 0, 0);
            _lblUptime.ForeColor = Color.DimGray;

            _lblTotals.Anchor = AnchorStyles.Left;
            _lblTotals.AutoSize = true;
            _lblTotals.Margin = new Padding(14, 0, 0, 0);
            _lblTotals.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            _cmbLang.Anchor = AnchorStyles.Right;
            _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLang.Margin = new Padding(16, 0, 2, 0);
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
            // Panel2 holds BOTH the day-log group and the master-edit group, docked Fill; exactly one
            // is Visible at a time (MainForm toggles them). _grpMaster is added last so it sits on top
            // when shown; it starts hidden.
            _split.Panel2.Controls.Add(_grpLogs);
            _split.Panel2.Controls.Add(_grpMaster);
            // The left group boxes live in a TableLayoutPanel, which insets each cell by its default
            // 3px margin — so _grpClients starts 3px below Panel1's top. _grpLogs is docked straight
            // into Panel2 (a plain Panel ignores Margin), so without this it sits 3px HIGHER than the
            // left panel. Padding(3) on Panel2 mirrors that inset on all four edges → the two group
            // boxes' top borders line up exactly (measured: both at the same y).
            _split.Panel2.Padding = new Padding(3);

            // _leftLayout — master toolbar (40px) over the clients group (230px) over the
            // CSV-uploads group (fills the rest).
            _leftLayout.ColumnCount = 1;
            _leftLayout.RowCount = 3;
            _leftLayout.Dock = DockStyle.Fill;
            _leftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230F));
            _leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _leftLayout.Controls.Add(_masterBar, 0, 0);
            _leftLayout.Controls.Add(_grpClients, 0, 1);
            _leftLayout.Controls.Add(_grpCsv, 0, 2);

            // _masterBar — the master toolbar sitting directly above the devices list. Three
            // AutoSize'd buttons anchored left: [Customer master][Item master] open a master in the
            // right panel for editing; [Sync master] arms the current masters for the next phone sync.
            _masterBar.Dock = DockStyle.Fill;
            _masterBar.Margin = new Padding(3, 3, 3, 0);
            _masterBar.Padding = new Padding(0);
            _masterBar.ColumnCount = 4;
            _masterBar.RowCount = 1;
            _masterBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _masterBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 0 Customer
            _masterBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 1 Item
            _masterBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 2 Sync
            _masterBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // 3 flexible gap
            _masterBar.Controls.Add(_btnMasterCustomer, 0, 0);
            _masterBar.Controls.Add(_btnMasterItem, 1, 0);
            _masterBar.Controls.Add(_btnSyncMaster, 2, 0);

            _btnMasterCustomer.Anchor = AnchorStyles.Left;
            _btnMasterCustomer.AutoSize = true;
            _btnMasterCustomer.Margin = new Padding(0, 0, 6, 0);
            _btnMasterCustomer.Text = "Customer master";

            _btnMasterItem.Anchor = AnchorStyles.Left;
            _btnMasterItem.AutoSize = true;
            _btnMasterItem.Margin = new Padding(0, 0, 6, 0);
            _btnMasterItem.Text = "Item master";

            _btnSyncMaster.Anchor = AnchorStyles.Left;
            _btnSyncMaster.AutoSize = true;
            _btnSyncMaster.Margin = new Padding(0, 0, 6, 0);
            _btnSyncMaster.ForeColor = Color.SteelBlue;
            _btnSyncMaster.Text = "Sync master";

            // _grpClients — server-state label (top) + the clients grid.
            _grpClients.Dock = DockStyle.Fill;
            _grpClients.Padding = new Padding(6);
            _grpClients.Text = "Bluetooth devices";
            _grpClients.Controls.Add(_clientsLayout);

            _clientsLayout.ColumnCount = 1;
            _clientsLayout.RowCount = 2;
            _clientsLayout.Dock = DockStyle.Fill;
            _clientsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // Row 0 height matches _logsLayout's filter row (right panel) so the server-status label
            // and the day-log filter bar sit on the SAME baseline across the two panels.
            _clientsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
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
            // 34px matches _clientsLayout row 0 (left panel) so the filter bar lines up with the
            // server-status label on the same baseline. Controls inside are anchored → auto-centred.
            _logsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            _logsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _logsLayout.Controls.Add(_filterRow, 0, 0);
            _logsLayout.Controls.Add(_dgvLogs, 0, 1);

            // _filterRow — a single-row grid so every control lines up on one baseline. Each control
            // is AutoSize'd and anchored (Left for the filter group, Right for Export); the grid
            // centres them vertically, so there are NO hand-tuned top-padding offsets to keep in sync.
            // Columns: [date lbl][‹][picker][›] · [type lbl][monitor][pallet][direct] · [flex gap][Export]
            _filterRow.Dock = DockStyle.Fill;
            _filterRow.Margin = new Padding(0);
            _filterRow.ColumnCount = 10;
            _filterRow.RowCount = 1;
            _filterRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 8; i++)
                _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // flexible gap
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // Export button
            _filterRow.Controls.Add(_lblDate, 0, 0);
            _filterRow.Controls.Add(_btnPrevDay, 1, 0);
            _filterRow.Controls.Add(_dtpDay, 2, 0);
            _filterRow.Controls.Add(_btnNextDay, 3, 0);
            _filterRow.Controls.Add(_lblTypeFilter, 4, 0);
            _filterRow.Controls.Add(_rbMonitor, 5, 0);
            _filterRow.Controls.Add(_rbPallet, 6, 0);
            _filterRow.Controls.Add(_rbDirect, 7, 0);
            _filterRow.Controls.Add(_btnExportDay, 9, 0);

            _lblDate.Anchor = AnchorStyles.Left;
            _lblDate.AutoSize = true;
            _lblDate.Margin = new Padding(2, 0, 6, 0);
            _lblDate.Text = "Date:";

            _btnPrevDay.Anchor = AnchorStyles.Left;
            _btnPrevDay.AutoSize = false;
            _btnPrevDay.Size = new Size(30, 26);
            _btnPrevDay.Margin = new Padding(0);
            _btnPrevDay.Text = "‹";

            _dtpDay.Anchor = AnchorStyles.Left;
            _dtpDay.Format = DateTimePickerFormat.Short;
            _dtpDay.Margin = new Padding(4, 0, 4, 0);
            _dtpDay.Width = 120;

            _btnNextDay.Anchor = AnchorStyles.Left;
            _btnNextDay.AutoSize = false;
            _btnNextDay.Size = new Size(30, 26);
            _btnNextDay.Margin = new Padding(0);
            _btnNextDay.Text = "›";

            _lblTypeFilter.Anchor = AnchorStyles.Left;
            _lblTypeFilter.AutoSize = true;
            _lblTypeFilter.Margin = new Padding(20, 0, 6, 0);
            _lblTypeFilter.Text = "Type:";

            _rbMonitor.Anchor = AnchorStyles.Left;
            _rbMonitor.AutoSize = true;
            _rbMonitor.Checked = true;
            _rbMonitor.Margin = new Padding(0, 0, 12, 0);
            _rbMonitor.Text = "Monitor";

            _rbPallet.Anchor = AnchorStyles.Left;
            _rbPallet.AutoSize = true;
            _rbPallet.Margin = new Padding(0, 0, 12, 0);
            _rbPallet.Text = "Pallet";

            _rbDirect.Anchor = AnchorStyles.Left;
            _rbDirect.AutoSize = true;
            _rbDirect.Margin = new Padding(0, 0, 12, 0);
            _rbDirect.Text = "Direct";

            // _btnExportDay — pinned to the right edge; exports exactly the rows/columns shown below.
            _btnExportDay.Anchor = AnchorStyles.Right;
            _btnExportDay.AutoSize = true;
            _btnExportDay.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnExportDay.Padding = new Padding(8, 3, 8, 3);
            _btnExportDay.Margin = new Padding(6, 0, 2, 0);
            _btnExportDay.Text = "Export CSV";

            ConfigureGrid(_dgvLogs);
            _dgvLogs.AutoGenerateColumns = true;
            // Fill mode: columns stretch to the right edge of the window. Per-column proportions
            // (FillWeight) are assigned by header in MainForm.ApplyLogColumnWeights so wide columns
            // (品目明細) get more % and short ones (#, 状態) less.
            _dgvLogs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            // _grpMaster — the master-edit panel (hidden until a master button is clicked). Mirrors
            // _grpLogs's insets so its top border lines up with the left panel's.
            _grpMaster.Dock = DockStyle.Fill;
            _grpMaster.Padding = new Padding(6);
            _grpMaster.Visible = false;
            _grpMaster.Text = "Master";
            _grpMaster.Controls.Add(_masterLayout);

            _masterLayout.ColumnCount = 1;
            _masterLayout.RowCount = 2;
            _masterLayout.Dock = DockStyle.Fill;
            _masterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // 34px matches the day-log filter bar so the two panels' toolbars share a baseline.
            _masterLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            _masterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _masterLayout.Controls.Add(_masterEditRow, 0, 0);
            _masterLayout.Controls.Add(_dgvMaster, 0, 1);

            // _masterEditRow — [title] · [flex gap] · [Add][Delete][Save][Close], all anchored so the
            // grid centres them on one baseline.
            _masterEditRow.Dock = DockStyle.Fill;
            _masterEditRow.Margin = new Padding(0);
            _masterEditRow.ColumnCount = 6;
            _masterEditRow.RowCount = 1;
            _masterEditRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 0 title
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // 1 flex gap
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 2 Add
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 3 Delete
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 4 Save
            _masterEditRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 5 Close
            _masterEditRow.Controls.Add(_lblMasterTitle, 0, 0);
            _masterEditRow.Controls.Add(_btnMasterAdd, 2, 0);
            _masterEditRow.Controls.Add(_btnMasterDelete, 3, 0);
            _masterEditRow.Controls.Add(_btnMasterSave, 4, 0);
            _masterEditRow.Controls.Add(_btnMasterClose, 5, 0);

            _lblMasterTitle.Anchor = AnchorStyles.Left;
            _lblMasterTitle.AutoSize = true;
            _lblMasterTitle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _lblMasterTitle.Margin = new Padding(2, 0, 0, 0);
            _lblMasterTitle.Text = "Master";

            _btnMasterAdd.Anchor = AnchorStyles.Right;
            _btnMasterAdd.AutoSize = true;
            _btnMasterAdd.Margin = new Padding(4, 0, 0, 0);
            _btnMasterAdd.Text = "Add row";

            _btnMasterDelete.Anchor = AnchorStyles.Right;
            _btnMasterDelete.AutoSize = true;
            _btnMasterDelete.Margin = new Padding(4, 0, 0, 0);
            _btnMasterDelete.Text = "Delete row";

            _btnMasterSave.Anchor = AnchorStyles.Right;
            _btnMasterSave.AutoSize = true;
            _btnMasterSave.Margin = new Padding(8, 0, 0, 0);
            _btnMasterSave.ForeColor = Color.SteelBlue;
            _btnMasterSave.Text = "Save";

            _btnMasterClose.Anchor = AnchorStyles.Right;
            _btnMasterClose.AutoSize = true;
            _btnMasterClose.Margin = new Padding(8, 0, 2, 0);
            _btnMasterClose.Text = "Close";

            // Editable grid (unlike the read-only ConfigureGrid grids): the operator edits cells,
            // adds and deletes rows, then Saves back to the master CSV.
            _dgvMaster.Dock = DockStyle.Fill;
            _dgvMaster.AllowUserToAddRows = true;
            _dgvMaster.AllowUserToDeleteRows = true;
            _dgvMaster.AllowUserToResizeRows = false;
            _dgvMaster.RowHeadersVisible = true;   // row selector makes "delete row" obvious
            _dgvMaster.AutoGenerateColumns = true;
            _dgvMaster.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _dgvMaster.MultiSelect = true;
            _dgvMaster.BackgroundColor = Color.White;
            _dgvMaster.BorderStyle = BorderStyle.None;
            _dgvMaster.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _dgvMaster.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _dgvMaster.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

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
            _masterBar.ResumeLayout(false);
            _masterBar.PerformLayout();
            _grpClients.ResumeLayout(false);
            _clientsLayout.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_dgvClients).EndInit();
            _grpCsv.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)_dgvCsv).EndInit();
            _grpLogs.ResumeLayout(false);
            _logsLayout.ResumeLayout(false);
            _filterRow.ResumeLayout(false);
            _filterRow.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)_dgvLogs).EndInit();
            ((System.ComponentModel.ISupportInitialize)_dgvMaster).EndInit();
            _masterEditRow.ResumeLayout(false);
            _masterEditRow.PerformLayout();
            _masterLayout.ResumeLayout(false);
            _grpMaster.ResumeLayout(false);
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
