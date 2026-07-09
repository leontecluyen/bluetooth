namespace LeontecSyncLogSystem.Services
{
    /// <summary>Bound from the "Sync" section of appsettings.json.</summary>
    public class SyncOptions
    {
        public const string SectionName = "Sync";

        /// <summary>
        /// SDP service name advertised by the Bluetooth SPP server. (The phone locates the
        /// PC by its Bluetooth <i>radio</i> name and connects by SPP UUID, so this is mostly
        /// cosmetic — but handy when browsing services.)
        /// </summary>
        public string BluetoothServiceName { get; set; } = "SyncLogServer";

        /// <summary>
        /// Folder where a faithful copy of every received CSV upload is written
        /// (<c>&lt;BackupFolder&gt;/&lt;yyyyMMdd&gt;/&lt;filename&gt;</c>). Empty ⇒ default
        /// <c>%LOCALAPPDATA%/LeontecSyncLogSystem/backup</c>. Resolved at startup and logged.
        /// </summary>
        public string BackupFolder { get; set; } = "";
    }

    public class DatabaseOptions
    {
        public const string SectionName = "Database";

        /// <summary>
        /// When true (default) the app runs its OWN bundled MariaDB (shipped under
        /// <c>&lt;app&gt;/mariadb/</c>) as a child process — no separate MySQL install needed on the
        /// target PC. When false, <see cref="ConnectionString"/> points at an external MySQL/MariaDB.
        /// </summary>
        public bool Embedded { get; set; } = true;

        /// <summary>TCP port for the embedded server (127.0.0.1 only). Kept off 3306 to avoid clashes.</summary>
        public int EmbeddedPort { get; set; } = 3307;

        /// <summary>
        /// Data directory for the embedded server. Empty ⇒
        /// <c>%LOCALAPPDATA%/LeontecSyncLogSystem/db-data</c>.
        /// </summary>
        public string DataDir { get; set; } = "";

        /// <summary>
        /// Connection string used only when <see cref="Embedded"/> is false (external MySQL/MariaDB).
        /// Example: <c>Server=localhost;Port=3306;Database=leontec_sync;User=root;Password=...;</c>
        /// </summary>
        public string ConnectionString { get; set; } =
            "Server=localhost;Port=3306;Database=leontec_sync;User=root;Password=;";
    }
}
