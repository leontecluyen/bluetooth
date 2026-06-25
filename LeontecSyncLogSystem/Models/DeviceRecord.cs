using System;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// Persisted record of a Bluetooth device the PC has seen, so the client list survives
    /// app restarts. Keyed by the device's Bluetooth address (stable per device). Mirrors the
    /// persistable parts of <c>BtClientStatus</c>; live "Connected" state is NOT stored
    /// (a freshly loaded device is offline until it reconnects).
    /// </summary>
    public class DeviceRecord
    {
        /// <summary>Bluetooth MAC address — primary key.</summary>
        public string Address { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? WorkerId { get; set; }

        public DateTime? FirstSeenUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public DateTime? LastFrameUtc { get; set; }
        public DateTime? LastHeartbeatUtc { get; set; }

        public long FramesReceived { get; set; }
        public long RecordsIngested { get; set; }
        public long Sessions { get; set; }
        public long Heartbeats { get; set; }
    }
}
