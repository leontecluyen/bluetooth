using System.IO;
using System.Xml.Linq;

namespace LeontecSyncLogSystem.UI
{
    /// <summary>
    /// Show/hide toggles for the dashboard, read from <c>configuration.xml</c> (see
    /// <see cref="Services.AppPaths.AppConfigPath"/>). <b>Every toggle defaults to <c>false</c>
    /// (hidden)</b> — a fresh install shows only the day-log table on the right; the operator opts
    /// each piece back in by editing the file.
    ///
    /// <para>File shape (toggles optional, missing ⇒ false; <c>language</c> defaults to <c>ja</c>):</para>
    /// <code>
    /// &lt;configuration&gt;
    ///   &lt;language&gt;ja&lt;/language&gt;
    ///   &lt;showResetButton&gt;false&lt;/showResetButton&gt;
    ///   &lt;showOpenBackupButton&gt;false&lt;/showOpenBackupButton&gt;
    ///   &lt;showLanguageButton&gt;false&lt;/showLanguageButton&gt;
    ///   &lt;showMasterButtons&gt;false&lt;/showMasterButtons&gt;
    ///   &lt;showBluetoothPanel&gt;false&lt;/showBluetoothPanel&gt;
    ///   &lt;showCsvPanel&gt;false&lt;/showCsvPanel&gt;
    ///   &lt;showMysqlStatus&gt;false&lt;/showMysqlStatus&gt;
    /// &lt;/configuration&gt;
    /// </code>
    /// </summary>
    public sealed class UiConfig
    {
        /// <summary>
        /// UI language, the authoritative source at startup: <c>ja</c> (default) or <c>en</c>. Applied
        /// via <see cref="Loc.SetLanguage"/> when the dashboard opens; a runtime change from the
        /// language combo (when shown) is written back here so this stays the single source of truth.
        /// </summary>
        public string Language { get; set; } = "ja";

        /// <summary>The <see cref="AppLang"/> for <see cref="Language"/> (anything but "en" ⇒ Japanese).</summary>
        public AppLang LanguageAsAppLang() =>
            string.Equals(Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? AppLang.En : AppLang.Ja;

        /// <summary>Toolbar "Reset" button (clears all data).</summary>
        public bool ShowResetButton { get; set; }

        /// <summary>Toolbar "Open backup folder" button.</summary>
        public bool ShowOpenBackupButton { get; set; }

        /// <summary>Toolbar language combo box.</summary>
        public bool ShowLanguageButton { get; set; }

        /// <summary>The two master buttons (Customer/Item) above the left panels.</summary>
        public bool ShowMasterButtons { get; set; }

        /// <summary>Top-left panel — the Bluetooth devices list ("panel 1").</summary>
        public bool ShowBluetoothPanel { get; set; }

        /// <summary>Bottom-left panel — the received-CSV list ("panel 2").</summary>
        public bool ShowCsvPanel { get; set; }

        /// <summary>Toolbar MySQL connection-status label.</summary>
        public bool ShowMysqlStatus { get; set; }

        /// <summary>
        /// Load toggles from <paramref name="path"/>; if the file is missing, write a default
        /// (all-false) one and return it. Any error is logged and falls back to all-false, so the
        /// dashboard always opens.
        /// </summary>
        public static UiConfig LoadOrCreate(string path, ILogger logger)
        {
            try
            {
                if (!File.Exists(path))
                {
                    var created = new UiConfig();
                    created.Save(path);
                    logger.LogWarning(
                        "UI config not found — created default (all hidden) {Path}. Set toggles to true to show parts.",
                        path);
                    return created;
                }

                var root = XDocument.Load(path).Root;
                var cfg = new UiConfig
                {
                    Language = ReadStr(root, "language", "ja"),
                    ShowResetButton = Read(root, "showResetButton"),
                    ShowOpenBackupButton = Read(root, "showOpenBackupButton"),
                    ShowLanguageButton = Read(root, "showLanguageButton"),
                    ShowMasterButtons = Read(root, "showMasterButtons"),
                    ShowBluetoothPanel = Read(root, "showBluetoothPanel"),
                    ShowCsvPanel = Read(root, "showCsvPanel"),
                    ShowMysqlStatus = Read(root, "showMysqlStatus"),
                };
                logger.LogInformation(
                    "Loaded UI config from {Path}: language={Lang} reset={R} backup={B} langBtn={L} master={M} bt={P1} csv={P2} mysql={S}.",
                    path, cfg.Language, cfg.ShowResetButton, cfg.ShowOpenBackupButton, cfg.ShowLanguageButton,
                    cfg.ShowMasterButtons, cfg.ShowBluetoothPanel, cfg.ShowCsvPanel, cfg.ShowMysqlStatus);
                return cfg;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read UI config {Path}; using defaults (all hidden).", path);
                return new UiConfig();
            }
        }

        /// <summary>Write the toggles to <paramref name="path"/> (creating the folder if needed).</summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            new XDocument(new XElement("configuration",
                new XElement("language", Language),
                new XElement("showResetButton", ShowResetButton),
                new XElement("showOpenBackupButton", ShowOpenBackupButton),
                new XElement("showLanguageButton", ShowLanguageButton),
                new XElement("showMasterButtons", ShowMasterButtons),
                new XElement("showBluetoothPanel", ShowBluetoothPanel),
                new XElement("showCsvPanel", ShowCsvPanel),
                new XElement("showMysqlStatus", ShowMysqlStatus)))
                .Save(path);
        }

        private static bool Read(XElement? root, string name)
        {
            var e = root?.Element(name);
            return e is not null && bool.TryParse(e.Value.Trim(), out var v) && v;
        }

        private static string ReadStr(XElement? root, string name, string fallback)
        {
            var e = root?.Element(name);
            return e is null || string.IsNullOrWhiteSpace(e.Value) ? fallback : e.Value.Trim();
        }
    }
}
