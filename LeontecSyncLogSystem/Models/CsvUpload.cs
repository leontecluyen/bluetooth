using System;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One received CSV upload (one Bluetooth frame = one sync) persisted to the DB so it
    /// survives app restarts. Belongs to one device (<see cref="DeviceAddress"/> → Devices).
    /// The exact received CSV text is kept in <see cref="RawCsv"/> so the rows (including any
    /// in-file duplicates) can be reproduced on demand by re-parsing — that is the
    /// "CSV has many rows" relation.
    /// </summary>
    public class CsvUpload
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Bluetooth address of the sending device (FK → Devices.Address).</summary>
        public string DeviceAddress { get; set; } = string.Empty;

        public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
        public string Source { get; set; } = "Bluetooth";
        public string Device { get; set; } = string.Empty;
        public string? WorkerId { get; set; }

        // --- Upload envelope (from the filename {type}_{yyyyMMdd}_{index}_{termId}.txt) ---
        /// <summary>"monitor_log" | "pallet_log" | "legacy" | "unknown".</summary>
        public string Type { get; set; } = "unknown";
        /// <summary>
        /// The LOG day taken from the filename (date-only, local calendar date). Drives the
        /// dashboard's per-day filter. Null for legacy uploads whose filename carried no date —
        /// the dashboard then falls back to the <see cref="ReceivedAtUtc"/> local date.
        /// </summary>
        public DateTime? LogDate { get; set; }
        /// <summary>Terminal id (temporarily the Android device name).</summary>
        public string TermId { get; set; } = string.Empty;
        /// <summary>Which time this (TermId, Type) has been sent (from the filename).</summary>
        public int UploadIndex { get; set; }
        /// <summary>True once a newer index of the same (TermId, Type) has arrived.</summary>
        public bool Superseded { get; set; }

        public int RowCount { get; set; }
        public int Inserted { get; set; }
        public int Duplicates { get; set; }

        /// <summary>The raw CSV payload exactly as received (header + rows, including duplicates).</summary>
        public string RawCsv { get; set; } = string.Empty;
    }
}
