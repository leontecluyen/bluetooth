using System.Globalization;

namespace LeontecSyncLogSystem.UI
{
    /// <summary>UI languages offered by the dashboard. English is the fallback.</summary>
    public enum AppLang
    {
        En,
        Ja,
    }

    /// <summary>
    /// Tiny in-process localization for the WinForms dashboard. Strings live in per-language
    /// dictionaries keyed by a stable code; <see cref="T(string)"/> returns the current language's
    /// text (falling back to the key itself if a translation is missing). The chosen language is
    /// persisted (see <see cref="LanguageStore"/>); on first run it is derived from the OS UI
    /// culture (ja → Japanese, anything else → English).
    ///
    /// Call <see cref="SetLanguage"/> to switch at runtime; subscribers to <see cref="Changed"/>
    /// (the dashboard) re-apply all visible texts without a restart.
    /// </summary>
    public static class Loc
    {
        /// <summary>Raised after the active language changes so the UI can re-apply its texts.</summary>
        public static event Action? Changed;

        public static AppLang Current { get; private set; }

        private static Dictionary<string, string> _map;

        static Loc()
        {
            Current = LanguageStore.Load() ?? DetectSystemLanguage();
            _map = MapFor(Current);
        }

        /// <summary>Switch the active language at runtime, persist it, and notify subscribers.</summary>
        public static void SetLanguage(AppLang lang)
        {
            if (lang == Current && _map.Count > 0) { /* still re-apply below */ }
            Current = lang;
            _map = MapFor(lang);
            LanguageStore.Save(lang);
            Changed?.Invoke();
        }

        /// <summary>Localized string for <paramref name="key"/> (returns the key if unknown).</summary>
        public static string T(string key) => _map.TryGetValue(key, out var v) ? v : key;

        /// <summary>Localized, <see cref="string.Format(string, object?[])"/>-formatted string.</summary>
        public static string T(string key, params object[] args) => string.Format(T(key), args);

        private static AppLang DetectSystemLanguage() =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
            {
                "ja" => AppLang.Ja,
                _ => AppLang.En,
            };

        private static Dictionary<string, string> MapFor(AppLang lang) => lang switch
        {
            AppLang.Ja => Ja,
            _ => En,
        };

        // ---------------- English (fallback) ----------------
        private static readonly Dictionary<string, string> En = new()
        {
            ["btn_refresh"] = "Refresh",
            ["btn_clear"] = "Reset",
            ["btn_export"] = "Export CSV",
            ["btn_supply_export"] = "補給データ出力",
            ["btn_open_backup"] = "Open backup folder",
            ["backup_open_error"] = "Cannot open the backup folder: {0}",
            ["conn_starting"] = "● Starting…",
            ["conn_running"] = "● RUNNING",
            ["conn_error"] = "● ERROR ({0})",
            ["uptime"] = "Uptime: {0}",
            ["totals"] = "Logs today: {0}   |   Total: {1}",
            ["bt_listening"] = "● Bluetooth SPP: LISTENING  —  PC name to pair: \"{0}\"",
            ["bt_not_listening"] = "× Bluetooth SPP: NOT LISTENING  —  {0}",
            ["unknown"] = "(unknown)",
            ["bt_initializing"] = "initializing / checking the Bluetooth adapter",
            ["grp_clients"] = "Bluetooth devices (one machine = one SPP connection)",
            ["grp_csv"] = "Received CSVs",
            ["grp_daylog"] = "Day log",
            ["col_name"] = "Machine",
            ["col_device"] = "Device",
            ["col_presence"] = "Presence",
            ["col_frames"] = "Frames",
            ["col_records"] = "Records",
            ["col_last_hb"] = "Last beat",
            ["col_last_data"] = "Last data",
            ["col_time"] = "Time",
            ["col_type"] = "Type",
            ["col_index"] = "No.",
            ["col_rows"] = "Rows",
            ["online"] = "● Online",
            ["offline"] = "○ Offline",
            ["clear_confirm_body"] =
                "Delete ALL logs in the database, the received-CSV list and the device list?\n" +
                "Use this to re-test from scratch. This cannot be undone.\n" +
                "(Note: this does NOT unpair Bluetooth in Windows — it only clears data/state in the app.)",
            ["clear_confirm_title"] = "Confirm: clear all data",
            ["clear_done"] = "Deleted {0} log rows and all received CSVs.",
            ["done"] = "Done",
            ["clear_error"] = "Error clearing the database: {0}",
            ["error"] = "Error",
            ["type_monitor"] = "Monitor (入出庫)",
            ["type_pallet"] = "Pallet (パレット)",
            ["type_direct"] = "Direct (直送)",
            ["type_legacy"] = "Legacy",
            ["type_other"] = "Other",
            ["lbl_date"] = "Data date:",
            ["lbl_type"] = "Type:",
            ["daylog_header"] = "{0} — {1} — {2} rows",
            ["daylog_dups"] = ", {0} duplicate rows",
            ["err_load_daylog"] = "Error loading day log: {0}",
            ["daylog_db_error"] = "Day log — cannot reach the database (is MySQL running?)",
            ["export_done"] = "Exported {0} rows to:\n{1}",
            ["export_empty"] = "Nothing to export (no rows shown).",
            ["export_error"] = "Export failed: {0}",
            ["menu_language"] = "Language",
            ["mysql_checking"] = "● MySQL: checking…",
            ["mysql_connected"] = "● MySQL: connected ({0})",
            ["mysql_disconnected"] = "× MySQL: disconnected ({0})",

            // Master editing (customer / item master files, editable; pulled by the phone).
            ["btn_master_customer"] = "Customer master",
            ["btn_master_item"] = "Item master",
            ["btn_master_add"] = "Add row",
            ["btn_master_delete"] = "Delete row",
            ["btn_master_save"] = "Save",
            ["btn_master_close"] = "Close",
            ["grp_master"] = "Master",
            ["master_customer"] = "Customer master",
            ["master_item"] = "Item master",
            ["master_load_error"] = "Cannot load the master file: {0}",
            ["master_saved"] = "Saved {0} — {1} rows.",
            ["master_save_error"] = "Save failed: {0}",
            ["master_discard_confirm"] = "Discard unsaved changes to the master?",
        };

        // ---------------- 日本語 ----------------
        private static readonly Dictionary<string, string> Ja = new()
        {
            ["btn_refresh"] = "更新",
            ["btn_clear"] = "リセット",
            ["btn_export"] = "CSV出力",
            ["btn_supply_export"] = "補給データ出力",
            ["btn_open_backup"] = "バックアップフォルダを開く",
            ["backup_open_error"] = "バックアップフォルダを開けません: {0}",
            ["conn_starting"] = "● 起動中…",
            ["conn_running"] = "● 稼働中",
            ["conn_error"] = "● エラー ({0})",
            ["uptime"] = "稼働時間: {0}",
            ["totals"] = "本日のログ: {0}   |   合計: {1}",
            ["bt_listening"] = "● Bluetooth SPP: 受信待機中  —  ペアリング用PC名: \"{0}\"",
            ["bt_not_listening"] = "× Bluetooth SPP: 待機していません  —  {0}",
            ["unknown"] = "(不明)",
            ["bt_initializing"] = "起動中 / Bluetoothアダプタを確認中",
            ["grp_clients"] = "Bluetoothデバイス（1台 = 1 SPP接続）",
            ["grp_csv"] = "受信したCSV",
            ["grp_daylog"] = "当日のログ",
            ["col_name"] = "端末名",
            ["col_device"] = "デバイス",
            ["col_presence"] = "状態",
            ["col_frames"] = "フレーム",
            ["col_records"] = "レコード",
            ["col_last_hb"] = "最終ハートビート",
            ["col_last_data"] = "最終データ",
            ["col_time"] = "時刻",
            ["col_type"] = "種類",
            ["col_index"] = "回",
            ["col_rows"] = "行数",
            ["online"] = "● オンライン",
            ["offline"] = "○ オフライン",
            ["clear_confirm_body"] =
                "データベースの全ログ、受信CSV一覧、デバイス一覧をすべて削除しますか？\n" +
                "最初からテストし直す場合に使用します。この操作は元に戻せません。\n" +
                "（注意：WindowsのBluetoothペアリングは解除されません。アプリ内のデータ/状態のみ削除します。）",
            ["clear_confirm_title"] = "全データ削除の確認",
            ["clear_done"] = "{0} 件のログと全受信CSVを削除しました。",
            ["done"] = "完了",
            ["clear_error"] = "DB削除エラー: {0}",
            ["error"] = "エラー",
            ["type_monitor"] = "モニター (入出庫)",
            ["type_pallet"] = "パレット",
            ["type_direct"] = "直送",
            ["type_legacy"] = "レガシー",
            ["type_other"] = "その他",
            ["lbl_date"] = "データ日",
            ["lbl_type"] = "種類:",
            ["daylog_header"] = "{0} — {1} — {2} 行",
            ["daylog_dups"] = "、重複 {0} 行",
            ["err_load_daylog"] = "当日ログ読込エラー: {0}",
            ["daylog_db_error"] = "当日のログ — データベースに接続できません（MySQLは起動中ですか？）",
            ["export_done"] = "{0} 行をエクスポートしました:\n{1}",
            ["export_empty"] = "エクスポートする行がありません。",
            ["export_error"] = "エクスポート失敗: {0}",
            ["menu_language"] = "言語",
            ["mysql_checking"] = "● MySQL: 確認中…",
            ["mysql_connected"] = "● MySQL: 接続済み ({0})",
            ["mysql_disconnected"] = "× MySQL: 未接続 ({0})",

            // マスタ編集（顧客・品目マスタの編集。端末がプルして取得）
            ["btn_master_customer"] = "顧客マスタ",
            ["btn_master_item"] = "品目マスタ",
            ["btn_master_add"] = "行追加",
            ["btn_master_delete"] = "行削除",
            ["btn_master_save"] = "保存",
            ["btn_master_close"] = "閉じる",
            ["grp_master"] = "マスタ",
            ["master_customer"] = "顧客マスタ",
            ["master_item"] = "品目マスタ",
            ["master_load_error"] = "マスタファイルを読み込めません: {0}",
            ["master_saved"] = "{0} を保存しました — {1} 行。",
            ["master_save_error"] = "保存に失敗しました: {0}",
            ["master_discard_confirm"] = "マスタの未保存の変更を破棄しますか？",
        };
    }

    /// <summary>
    /// Persists the chosen UI language to a small file under the user's local app-data so it
    /// survives restarts. Returns null when nothing valid was stored (→ derive from the OS).
    /// </summary>
    internal static class LanguageStore
    {
        private static string FilePath => Services.AppPaths.AppDataFile("ui-language.txt");

        public static AppLang? Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var text = File.ReadAllText(FilePath).Trim();
                return Enum.TryParse<AppLang>(text, ignoreCase: true, out var lang) ? lang : null;
            }
            catch { return null; }
        }

        public static void Save(AppLang lang)
        {
            try { File.WriteAllText(FilePath, lang.ToString()); }
            catch { /* non-fatal: language just won't persist this run */ }
        }
    }
}
