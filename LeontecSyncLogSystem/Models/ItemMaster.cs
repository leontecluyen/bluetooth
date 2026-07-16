namespace LeontecSyncLogSystem.Models
{
    /// <summary>
    /// One row of the 品目マスタ (item master), imported from <c>item_master.csv</c> (the same file
    /// <see cref="Services.IMasterStore"/> owns, seeded from the Android assets). Columns mirror the
    /// CSV header <c>品目コード,品目名称,箱種,品目名称_2</c>.
    ///
    /// <para>Used by the 直送 supply export to resolve a トヨタ 品番 to its ヨコオ品番: the 品番 is dashed
    /// (e.g. <c>0860900150</c> → <c>08609-00150</c>) and matched against <see cref="Name"/> with a
    /// <c>LIKE '%…%'</c> search — the matched 品目名称 IS the ヨコオ品番 (see
    /// <see cref="Monitoring.MonitorService.GetDirectSupplyExportAsync"/>).</para>
    /// </summary>
    public class ItemMaster
    {
        /// <summary>Surrogate numeric primary key (auto-increment).</summary>
        public long Id { get; set; }

        /// <summary>品目コード — the item code (CSV column 1).</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>品目名称 — the item name (CSV column 2); the ヨコオ品番 lookup matches against this.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>箱種 — box type (CSV column 3), kept for faithfulness to the source CSV.</summary>
        public string? BoxType { get; set; }

        /// <summary>品目名称_2 — the secondary name (CSV column 4), kept for faithfulness.</summary>
        public string? SubName { get; set; }
    }
}
