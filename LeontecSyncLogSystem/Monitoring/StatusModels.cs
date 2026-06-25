namespace LeontecSyncLogSystem.Monitoring
{
    // Mirrors the JSON returned by the service's GET /api/status endpoint.

    public class StatusDto
    {
        public DateTime ServerTimeUtc { get; set; }
        public DateTime StartedUtc { get; set; }
        public long UptimeSeconds { get; set; }
        public BtServerDto BtServer { get; set; } = new();
        public List<ClientDto> Clients { get; set; } = new();
        public LogsDto Logs { get; set; } = new();
        // CSV uploads are fetched per-device on demand (MonitorService.GetCsvsForDeviceAsync),
        // not embedded in the periodic snapshot.
    }

    /// <summary>State of the Bluetooth SPP server itself.</summary>
    public class BtServerDto
    {
        public bool Listening { get; set; }
        /// <summary>The PC's Bluetooth radio name — the phone's pcBluetoothName must match this.</summary>
        public string? RadioName { get; set; }
        public string? ServiceName { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>One connected/seen Bluetooth client = one Android device.</summary>
    public class ClientDto
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        /// <summary>WorkerId reported in the device's CSV (null until first frame).</summary>
        public string? WorkerId { get; set; }
        public bool Connected { get; set; }
        /// <summary>"Connected" / "Disconnected" — momentary RFCOMM state, convenience for the grid.</summary>
        public string State => Connected ? "Connected" : "Disconnected";
        /// <summary>Present = connected or seen (data/heartbeat) within the heartbeat timeout.</summary>
        public bool Online { get; set; }
        /// <summary>"Online" / "Offline" — heartbeat-based presence, convenience for the grid.</summary>
        public string Presence => Online ? "Online" : "Offline";
        public DateTime? LastFrameUtc { get; set; }
        /// <summary>Last contact of any kind (data or heartbeat).</summary>
        public DateTime? LastSeenUtc { get; set; }
        /// <summary>Last heartbeat ping received.</summary>
        public DateTime? LastHeartbeatUtc { get; set; }
        public long FramesReceived { get; set; }
        public long RecordsIngested { get; set; }
        public long Sessions { get; set; }
        public long Heartbeats { get; set; }
    }

    /// <summary>One received CSV upload (one Bluetooth sync) shown in the left-hand list.</summary>
    public class ReceivedCsvDto
    {
        public Guid Id { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
        public string Source { get; set; } = "";
        public string Device { get; set; } = "";
        /// <summary>Bluetooth address of the sending device — used to filter the list by device.</summary>
        public string Address { get; set; } = "";
        public string? WorkerId { get; set; }
        /// <summary>"monitor_log" | "pallet_log" | "legacy" | "unknown".</summary>
        public string Type { get; set; } = "unknown";
        public string TermId { get; set; } = "";
        /// <summary>Which time this (TermId, Type) was sent.</summary>
        public int UploadIndex { get; set; }
        /// <summary>Log day parsed from the filename (null for legacy uploads without a date).</summary>
        public DateTime? LogDate { get; set; }
        /// <summary>True if a newer index of the same (TermId, Type) has arrived.</summary>
        public bool Superseded { get; set; }
        public int RowCount { get; set; }
        public int Inserted { get; set; }
        public int Duplicates { get; set; }
        // Rows are fetched on demand (MonitorService.GetCsvTableAsync) when this CSV is selected.
    }

    /// <summary>A CSV rendered as a table: column headers (row 1) + data rows. Type-agnostic.</summary>
    public class CsvTableDto
    {
        public List<string> Headers { get; set; } = new();
        public List<string[]> Rows { get; set; } = new();
    }

    public class LogsDto
    {
        public int Total { get; set; }
        public int Today { get; set; }
    }

    public class LogDto
    {
        public Guid LogId { get; set; }
        public string WorkerId { get; set; } = "";
        public string JobType { get; set; } = "";
        public string BarcodeData { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string SyncMethod { get; set; } = "";
    }
}
