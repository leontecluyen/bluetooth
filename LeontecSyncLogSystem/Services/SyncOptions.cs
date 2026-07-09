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
        /// <c>&lt;root&gt;/_backup</c> (sibling of the app folder). Resolved at startup and logged.
        /// </summary>
        public string BackupFolder { get; set; } = "";
    }
}
