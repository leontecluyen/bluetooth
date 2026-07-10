using System.Text;
using LeontecSyncLogSystem.Models;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>Recognised CSV log types (detected from the row-1 header).</summary>
    public enum CsvType
    {
        Unknown = 0,
        /// <summary>入出庫 monitor log (モニタリスト単位).</summary>
        MonitorLog,
        /// <summary>パレット (pallet) log (パレット単位).</summary>
        PalletLog,
        /// <summary>直送管理 direct-delivery log (直送管理単位).</summary>
        DirectLog,
    }

    /// <summary>
    /// Central definition of the CSV log types: their canonical row-1 headers (constants), how to
    /// detect a type from a header line, how to read the term_id/index/type out of the upload
    /// filename, and how to parse each type's rows into normalized entities.
    ///
    /// Wire convention: the Bluetooth frame's FIRST line is the filename
    /// <c>{type}_{yyyyMMdd}_{termId}_{index}.txt</c> (e.g. <c>monitor_log_20260622_GalaxyS10_3.txt</c>);
    /// the rest is the CSV whose first line is the header.
    /// </summary>
    public static class CsvTypes
    {
        // --- Canonical headers (row 1) per type — keep in sync with the Android writer/docs ---
        // 状態 codes: monitor 0=正常/9=削除; pallet 0=正常/1=移動/9=削除.
        // monitor (モニタリスト単位, 9 cols): 積込箱数 before the trailing 状態 (= status code).
        public const string MonitorHeader =
            "開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,積込箱数,状態";
        // pallet (パレット単位, 7 cols): 品目明細 = space-separated 品目コード:箱数x数量; trailing 状態 code.
        public const string PalletHeader =
            "開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態";
        // direct (直送管理単位, 11 cols): one row per completed 照合 (no 状態 column — always shown).
        public const string DirectHeader =
            "開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番";

        public static string TypeKey(CsvType t) => t switch
        {
            CsvType.MonitorLog => "monitor_log",
            CsvType.PalletLog => "pallet_log",
            CsvType.DirectLog => "direct_log",
            _ => "unknown",
        };

        public static CsvType FromKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
        {
            "monitor_log" or "monitor" => CsvType.MonitorLog,
            "pallet_log" or "pallet" => CsvType.PalletLog,
            "direct_log" or "direct" => CsvType.DirectLog,
            _ => CsvType.Unknown,
        };

        /// <summary>
        /// Detect the type from the CSV's first (header) line — authoritative. Uses tokens unique
        /// to each type so it survives column reordering.
        /// </summary>
        public static CsvType DetectType(string headerLine)
        {
            var h = headerLine.Trim();
            // direct first: its unique tokens (納入先/工場コード/ヨコオ品番) don't appear in the others.
            if (h.Contains("納入先") || h.Contains("工場コード") || h.Contains("ヨコオ品番")) return CsvType.DirectLog;
            if (h.Contains("PLNo.") || h.Contains("品目明細")) return CsvType.PalletLog;
            if (h.Contains("入出庫伝票番号")) return CsvType.MonitorLog;
            return CsvType.Unknown;
        }

        /// <summary>True if a line looks like an upload filename (not a CSV header).</summary>
        public static bool IsFilenameLine(string line)
        {
            var l = line.Trim();
            if (l.Length == 0) return false;
            if (DetectType(l) != CsvType.Unknown) return false; // it's actually a CSV header
            return l.Contains("__") || l.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                                    || l.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse the upload filename into its envelope fields. Two formats are accepted:
        ///
        ///  • <b>Current</b> <c>{type}_{yyyyMMdd}_{termId}_{index}.txt</c>
        ///    (e.g. <c>monitor_log_20260622_GalaxyS10_3.txt</c>) — date = LOG day (drives the
        ///    per-day filter); <c>termId</c> comes BEFORE the trailing numeric <c>index</c>. Since
        ///    <c>type</c> contains an underscore and <c>termId</c> may too, we anchor on the 8-digit
        ///    date and take the LAST <c>_&lt;digits&gt;</c> as the index (term = everything between).
        ///  • <b>Legacy</b> <c>{type}__{index}__{termId}.csv</c> (double-underscore, no date) — kept
        ///    for backward compatibility with very old uploads.
        ///
        /// The type is cross-checked elsewhere against the CSV's own row-1 header (authoritative).
        /// </summary>
        public static (CsvType type, int index, string termId, DateOnly? date) ParseFilename(string filename)
        {
            var name = filename.Trim();
            // strip extension
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];

            // Legacy double-underscore envelope first (no date): {type}__{index}__{termId}.
            if (name.Contains("__"))
            {
                var parts = name.Split(new[] { "__" }, StringSplitOptions.None);
                if (parts.Length >= 3)
                {
                    var ltype = FromKey(parts[0]);
                    int.TryParse(parts[1], out var lidx);
                    var lterm = string.Join("__", parts.Skip(2));
                    return (ltype, lidx, lterm, null);
                }
                return (CsvType.Unknown, 0, "", null);
            }

            // Current format: ^(type)_(yyyyMMdd)_(termId)_(index)$  — term greedy, index = the
            // trailing digit group, so a term that itself ends in digits still parses correctly.
            var m = System.Text.RegularExpressions.Regex.Match(
                name, @"^(?<type>.+?)_(?<date>\d{8})_(?<term>.+)_(?<idx>\d+)$");
            if (m.Success)
            {
                var type = FromKey(m.Groups["type"].Value);
                int.TryParse(m.Groups["idx"].Value, out var idx);
                var term = m.Groups["term"].Value;
                DateOnly? date = DateOnly.TryParseExact(
                    m.Groups["date"].Value, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                return (type, idx, term, date);
            }

            return (CsvType.Unknown, 0, "", null);
        }

        // ----- Row parsers -----

        /// <summary>
        /// Parse the monitor (モニタリスト単位) CSV body into entries (skips header + blank lines).
        /// Current 9-col layout:
        /// 開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,積込箱数,状態(code 0/9).
        /// The older 8-col layout (no 積込箱数, 状態 at index 7) is still parsed for backward compat.
        /// </summary>
        public static List<MonitorEntry> ParseMonitor(string csv)
        {
            var result = new List<MonitorEntry>();
            foreach (var f in DataRows(csv, expectFirst: "開始時刻"))
            {
                if (f.Count < 8) continue;
                var e = new MonitorEntry
                {
                    StartTime = ToTime(f[0]),
                    EndTime = ToTime(f[1]),
                    SlipNo = f[2],
                    CustomerCode = f[3],
                    ItemCode = f[4],
                    Boxes = ToInt(f[5]),    // 箱数
                    Quantity = ToInt(f[6]), // 数量
                };
                if (f.Count >= 9)
                {
                    e.LoadedBoxes = ToInt(f[7]); // 積込箱数
                    e.StatusCode = f[8];          // 状態 code: 0=正常, 9=削除
                }
                else
                {
                    e.StatusCode = f[7];          // legacy 8-col: 状態 at index 7 (no 積込箱数)
                }
                result.Add(e);
            }
            return result;
        }

        /// <summary>
        /// Parse the pallet (パレット単位) CSV body. New 7-col layout (no 操作):
        /// 開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細,状態(code 0/1/9).
        /// </summary>
        public static List<PalletOp> ParsePallet(string csv)
        {
            var result = new List<PalletOp>();
            foreach (var f in DataRows(csv, expectFirst: "開始時刻"))
            {
                if (f.Count < 7) continue;
                var op = new PalletOp
                {
                    StartTime = ToTime(f[0]),
                    EndTime = ToTime(f[1]),
                    PlNo = f[2],
                    Customer = f[3],
                    DeliveryRun = f[4],
                    ItemDetailRaw = f[5],
                    StatusCode = f[6],     // 状態 code: 0=正常, 1=移動, 9=削除
                };
                op.Items = ParseItemDetail(f[5], op);
                result.Add(op);
            }
            return result;
        }

        /// <summary>
        /// Parse the direct-delivery (直送管理単位) CSV body into entries. 11-col layout:
        /// 開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番.
        /// One row per completed 照合 (no 状態 column).
        /// </summary>
        public static List<DirectEntry> ParseDirect(string csv)
        {
            var result = new List<DirectEntry>();
            foreach (var f in DataRows(csv, expectFirst: "開始時刻"))
            {
                if (f.Count < 11) continue;
                result.Add(new DirectEntry
                {
                    StartTime = ToTime(f[0]),
                    EndTime = ToTime(f[1]),
                    Customer = f[2],
                    DeliveryTo = f[3],
                    ShipDate = ToDate(f[4]),
                    PartNo = f[5],
                    Capacity = ToInt(f[6]),
                    Boxes = ToInt(f[7]),
                    DeliveryQty = ToInt(f[8]),
                    FactoryCode = f[9],
                    YokooPartNo = f[10],
                });
            }
            return result;
        }

        /// <summary>"50524:6x5 77729:50x10" → [{50524,6,5},{77729,50,10}].</summary>
        public static List<PalletOpItem> ParseItemDetail(string detail, PalletOp parent)
        {
            var items = new List<PalletOpItem>();
            foreach (var token in detail.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = token.IndexOf(':');
                if (colon <= 0) continue;
                var code = token[..colon];
                var rest = token[(colon + 1)..];
                var xpos = rest.IndexOf('x');
                if (xpos <= 0) continue;
                items.Add(new PalletOpItem
                {
                    PalletOp = parent,
                    ItemCode = code,
                    Boxes = ToInt(rest[..xpos]),
                    Quantity = ToInt(rest[(xpos + 1)..]),
                });
            }
            return items;
        }

        // ----- helpers -----

        /// <summary>Yields the split fields of each data row (skips a leading header row + blanks).</summary>
        private static IEnumerable<List<string>> DataRows(string csv, string expectFirst)
        {
            var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool first = true;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var fields = SplitCsv(raw);
                if (first)
                {
                    first = false;
                    if (fields.Count > 0 && fields[0].Trim() == expectFirst)
                        continue; // skip header
                }
                yield return fields;
            }
        }

        private static int ToInt(string s) =>
            int.TryParse(s.Trim(), out var v) ? v : 0;

        private static readonly string[] TimeFormats = { "HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm" };
        private static readonly string[] DateFormats = { "yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d" };

        /// <summary>Parse a time-of-day cell ("HH:mm:ss"). Blank/invalid → null.</summary>
        private static TimeOnly? ToTime(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            return TimeOnly.TryParseExact(s, TimeFormats,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var t)
                ? t
                : (TimeOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var t2) ? t2 : null);
        }

        /// <summary>Parse a date cell ("yyyy/MM/dd"). Blank/invalid → null.</summary>
        private static DateOnly? ToDate(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            return DateOnly.TryParseExact(s, DateFormats,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var d)
                ? d
                : (DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var d2) ? d2 : null);
        }

        /// <summary>Minimal RFC-4180-ish single-line CSV splitter (handles quoted fields).</summary>
        public static List<string> SplitCsv(string line)
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
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }
    }
}
