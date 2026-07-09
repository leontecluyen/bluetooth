using System;
using System.Collections.Generic;

namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One pallet operation row (パレット積込 / パレット間移動(元) / パレット間移動(先) / …),
    /// normalized. Belongs to one <see cref="CsvUpload"/>; has many <see cref="PalletOpItem"/>
    /// (the parsed 品目明細). The <see cref="OpType"/> (状態) is kept as the raw label so any
    /// operation — load, inter-pallet move source/dest, delete — is captured uniformly.
    /// </summary>
    public class PalletOp
    {
        public long Id { get; set; }
        public long UploadId { get; set; }

        public string OpType { get; set; } = "";        // 状態 (operation)
        public TimeOnly? StartTime { get; set; }         // 開始時刻 (time of day)
        public TimeOnly? EndTime { get; set; }           // 終了時刻
        public string PlNo { get; set; } = "";           // PLNo.
        public string Customer { get; set; } = "";       // 顧客
        public string DeliveryRun { get; set; } = "";    // 納入便
        public string ItemDetailRaw { get; set; } = "";  // 品目明細 (raw, e.g. "50524:6x5 77729:50x10")
        public string StatusCode { get; set; } = "";      // 状態 (code: 0 / 1 / 9)

        public List<PalletOpItem> Items { get; set; } = new();
    }

    /// <summary>One item line inside a pallet op's 品目明細 — 品目コード:箱数x数量.</summary>
    public class PalletOpItem
    {
        public long Id { get; set; }
        public long PalletOpId { get; set; }
        public PalletOp? PalletOp { get; set; }

        public string ItemCode { get; set; } = "";  // 品目コード
        public int Boxes { get; set; }               // 箱数
        public int Quantity { get; set; }            // 数量 (per box)
    }
}
