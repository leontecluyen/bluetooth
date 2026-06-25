using System;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// A single work/scan log record. Maps 1:1 to the centralized destination table.
    /// <para>
    /// The same record can arrive over two channels (Bluetooth SPP and the Wi-Fi
    /// backup API). <see cref="LogId"/> is the primary key and is used for
    /// deduplication so that re-transmits during connection drops are ignored.
    /// </para>
    /// </summary>
    public class LogEntry
    {
        /// <summary>Primary key. Generated on the device; stable across re-transmits.</summary>
        public Guid LogId { get; set; }

        /// <summary>Identifier of the worker / handheld device that produced the scan.</summary>
        public string WorkerId { get; set; } = string.Empty;

        /// <summary>Type of job (e.g. PICK, PACK, INSPECT).</summary>
        public string JobType { get; set; } = string.Empty;

        /// <summary>The scanned barcode payload.</summary>
        public string BarcodeData { get; set; } = string.Empty;

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        /// <summary>How this record reached the service: "Bluetooth" or "WiFi".</summary>
        public string SyncMethod { get; set; } = string.Empty;
    }
}
