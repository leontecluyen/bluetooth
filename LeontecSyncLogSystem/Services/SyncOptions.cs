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
    }

    public class DatabaseOptions
    {
        public const string SectionName = "Database";

        /// <summary>"Sqlite" | "SqlServer" | "Postgres".</summary>
        public string Provider { get; set; } = "Sqlite";

        public string ConnectionString { get; set; } = "Data Source=synclogs.db";
    }
}
