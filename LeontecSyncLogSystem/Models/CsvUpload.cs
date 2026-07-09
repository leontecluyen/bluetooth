using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One received CSV upload (one Bluetooth frame = one sync) persisted to the DB so it
    /// survives app restarts. Belongs to one device (<see cref="DeviceId"/> → Devices.Id).
    /// The exact received CSV text is kept in <see cref="RawCsv"/> so the rows (including any
    /// in-file duplicates) can be reproduced on demand by re-parsing — that is the
    /// "CSV has many rows" relation.
    /// </summary>
    public class CsvUpload
    {
        /// <summary>Surrogate numeric primary key (auto-increment).</summary>
        public long Id { get; set; }

        /// <summary>FK → Devices.Id (the sending device's surrogate key).</summary>
        public long DeviceId { get; set; }

        /// <summary>
        /// Bluetooth MAC of the sending device. NOT a mapped column — a transient carrier used to
        /// resolve <see cref="DeviceId"/> on the way in (ingest) and to surface the address on the
        /// way out (queries join Devices). The persisted link is <see cref="DeviceId"/>.
        /// </summary>
        [NotMapped]
        public string DeviceAddress { get; set; } = string.Empty;

        public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Which channel received it: "Bluetooth" | "WiFi".</summary>
        public string Source { get; set; } = "Bluetooth";

        // --- Upload envelope (from the filename {type}_{yyyyMMdd}_{termId}_{index}.txt) ---
        /// <summary>"monitor_log" | "pallet_log" | "direct_log" | "unknown".</summary>
        public string Type { get; set; } = "unknown";
        /// <summary>
        /// The LOG day taken from the filename (date-only). Drives the dashboard's per-day filter.
        /// Null for old uploads whose filename carried no date — the dashboard then falls back to
        /// the <see cref="ReceivedAtUtc"/> local date.
        /// </summary>
        public DateTime? LogDate { get; set; }
        /// <summary>Terminal id (temporarily the Android device name).</summary>
        public string TermId { get; set; } = string.Empty;
        /// <summary>Which time this (TermId, Type) has been sent (from the filename).</summary>
        public int UploadIndex { get; set; }
        /// <summary>True once a newer index of the same (TermId, Type) has arrived.</summary>
        public bool Superseded { get; set; }

        public int RowCount { get; set; }

        /// <summary>The raw CSV payload exactly as received (header + rows, including duplicates).</summary>
        public string RawCsv { get; set; } = string.Empty;
    }
}
