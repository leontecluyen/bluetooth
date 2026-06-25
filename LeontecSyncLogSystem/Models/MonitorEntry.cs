using System;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One row of a monitor (入出庫) CSV, normalized. Belongs to one <see cref="CsvUpload"/>.
    /// Columns mirror the canonical header
    /// 開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,積込箱数,状態.
    /// </summary>
    public class MonitorEntry
    {
        public long Id { get; set; }
        public Guid UploadId { get; set; }

        public string StartTime { get; set; } = "";    // 開始時刻 (HH:mm:ss)
        public string EndTime { get; set; } = "";       // 終了時刻
        public string SlipNo { get; set; } = "";         // 入出庫伝票番号
        public string CustomerCode { get; set; } = "";   // 顧客コード
        public string ItemCode { get; set; } = "";       // 品目コード
        public int Boxes { get; set; }                    // 箱数
        public int Quantity { get; set; }                 // 数量
        public int LoadedBoxes { get; set; }              // 積込箱数
        public string Status { get; set; } = "";          // 状態 (text, e.g. 〇 完了)
        public string StatusCode { get; set; } = "";       // 状態 (code: 0 / 9)
    }
}
