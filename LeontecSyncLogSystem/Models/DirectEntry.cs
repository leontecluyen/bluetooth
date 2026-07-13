using System;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One row of a direct-delivery (直送管理単位) CSV, normalized. Belongs to one
    /// <see cref="CsvUpload"/>. Columns mirror the canonical header (工場コード right after 納入先)
    /// 開始時刻,終了時刻,顧客,納入先,工場コード,出荷日,品番,収容数,箱数,納入数,ヨコオ品番.
    /// One row = one completed 照合 (there is no 状態 column — every row is shown).
    /// </summary>
    public class DirectEntry
    {
        public long Id { get; set; }
        public long UploadId { get; set; }

        public TimeOnly? StartTime { get; set; }         // 開始時刻 (time of day)
        public TimeOnly? EndTime { get; set; }           // 終了時刻
        public string Customer { get; set; } = "";       // 顧客
        public string DeliveryTo { get; set; } = "";     // 納入先
        public string FactoryCode { get; set; } = "";    // 工場コード (right after 納入先; トヨタ QR chars 23-30)
        public DateOnly? ShipDate { get; set; }          // 出荷日 (date)
        public string PartNo { get; set; } = "";         // 品番
        public int Capacity { get; set; }                 // 収容数
        public int Boxes { get; set; }                    // 箱数
        public int DeliveryQty { get; set; }              // 納入数
        public string YokooPartNo { get; set; } = "";    // ヨコオ品番
    }
}
