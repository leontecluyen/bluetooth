using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LeontecSyncLogSystem.Models;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Parses CSV produced by the Android device into <see cref="LogEntry"/> records.
    ///
    /// Expected column order (header row optional, case-insensitive):
    ///   LogId,WorkerId,JobType,BarcodeData,StartTime,EndTime
    ///
    /// Notes:
    ///  - <c>LogId</c> may be empty. When empty, a stable Guid is derived from the
    ///    record content so that re-transmits still deduplicate correctly.
    ///  - <c>SyncMethod</c> is NOT taken from the CSV; it is assigned by the channel
    ///    that received the data (Bluetooth vs WiFi).
    ///  - Fields may be wrapped in double quotes; embedded "" is an escaped quote.
    /// </summary>
    public static class CsvLogParser
    {
        private static readonly string[] DateFormats =
        {
            "yyyy-MM-ddTHH:mm:ss.fffffffK",
            "yyyy-MM-ddTHH:mm:ss.fffK",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "MM/dd/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
        };

        /// <summary>
        /// Parse a full CSV document (multiple lines) — used by the Wi-Fi backup API.
        /// </summary>
        public static IReadOnlyList<LogEntry> ParseDocument(string csv, string syncMethod)
        {
            var results = new List<LogEntry>();
            if (string.IsNullOrWhiteSpace(csv))
                return results;

            var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool first = true;

            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Skip a header row if present on the first non-empty line.
                if (first)
                {
                    first = false;
                    if (LooksLikeHeader(raw))
                        continue;
                }

                if (TryParseLine(raw, syncMethod, out var entry))
                    results.Add(entry!);
            }

            return results;
        }

        /// <summary>
        /// Parse a single CSV record — used by the Bluetooth SPP frame handler,
        /// where one STX..ETX frame contains exactly one record.
        /// </summary>
        public static bool TryParseLine(string line, string syncMethod, out LogEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var fields = SplitCsv(line);
            if (fields.Count < 6)
                return false;

            var workerId = fields[1].Trim();
            var jobType = fields[2].Trim();
            var barcode = fields[3].Trim();

            if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(barcode))
                return false;

            var start = ParseDate(fields[4]);
            var end = ParseDate(fields[5]);
            if (start is null)
                return false;

            // EndTime falls back to StartTime when the device leaves it blank.
            var endValue = end ?? start.Value;

            // LogId: use the supplied Guid when valid, otherwise derive a stable one.
            Guid logId;
            var rawId = fields[0].Trim();
            if (!Guid.TryParse(rawId, out logId))
                logId = DeriveDeterministicId(workerId, jobType, barcode, start.Value, endValue);

            entry = new LogEntry
            {
                LogId = logId,
                WorkerId = workerId,
                JobType = string.IsNullOrWhiteSpace(jobType) ? "UNKNOWN" : jobType,
                BarcodeData = barcode,
                StartTime = start.Value,
                EndTime = endValue,
                SyncMethod = syncMethod,
            };
            return true;
        }

        private static bool LooksLikeHeader(string line)
        {
            // The Android app emits header "id"; we also accept "LogId".
            var first = SplitCsv(line).FirstOrDefault()?.Trim();
            return string.Equals(first, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "LogId", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime? ParseDate(string value)
        {
            value = value.Trim();
            if (string.IsNullOrEmpty(value))
                return null;

            // Epoch milliseconds support (common for Android).
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs)
                && value.Length >= 10)
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime; }
                catch { /* out of range — fall through */ }
            }

            if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
                return exact;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose))
                return loose;

            return null;
        }

        /// <summary>
        /// Deterministic Guid (RFC 4122 v5-style, SHA-1 over content) so the same logical
        /// record always maps to the same key — enabling dedup even without a device LogId.
        /// </summary>
        private static Guid DeriveDeterministicId(
            string workerId, string jobType, string barcode, DateTime start, DateTime end)
        {
            var seed = $"{workerId}|{jobType}|{barcode}|{start:O}|{end:O}";
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            // Set version (5) and variant bits.
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
            return new Guid(guidBytes);
        }

        /// <summary>Minimal RFC-4180-ish CSV field splitter (single line).</summary>
        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                        inQuotes = true;
                    else if (c == ',')
                    {
                        fields.Add(sb.ToString());
                        sb.Clear();
                    }
                    else
                        sb.Append(c);
                }
            }

            fields.Add(sb.ToString());
            return fields;
        }
    }
}
