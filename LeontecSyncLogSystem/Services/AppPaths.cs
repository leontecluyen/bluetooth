using System.IO;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Resolves the app's on-disk layout — the SAME shape for both a debug run and a Release build,
    /// and NOT under <c>%LOCALAPPDATA%</c> anymore. The executable lives in an <c>app/</c> folder; the
    /// data folders and the two config files are siblings of that folder:
    ///
    /// <code>
    /// &lt;root&gt;/
    ///   _master/            editable master CSVs (customer/item)        → <see cref="MasterDir"/>
    ///   _backup/            per-day raw copies of received CSVs         → <see cref="BackupDir"/>
    ///   mysql.xml           external MySQL connection settings          → <see cref="MySqlConfigPath"/>
    ///   app/                the whole PC tool (this exe + its files)     → <see cref="AppDir"/>
    ///     configuration.xml UI show/hide toggles                        → <see cref="AppConfigPath"/>
    /// </code>
    ///
    /// <para><b>Anchor:</b> <see cref="AppDir"/> = the folder that holds the exe
    /// (<see cref="AppContext.BaseDirectory"/>); <see cref="RootDir"/> = its parent. In a dev build the
    /// exe sits in <c>bin/Release/net10.0-.../</c>, so the data folders land beside that folder — the
    /// exact same relative layout as a deployed <c>app/</c>.</para>
    /// </summary>
    public static class AppPaths
    {
        /// <summary>The folder that contains the executable (the deployed <c>app/</c> folder).</summary>
        public static string AppDir { get; } = AppContext.BaseDirectory;

        /// <summary>The parent of <see cref="AppDir"/> — holds <c>_master</c>, <c>_backup</c>, <c>mysql.xml</c>.</summary>
        public static string RootDir { get; } =
            new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName
            ?? AppContext.BaseDirectory;

        /// <summary>Editable master CSVs (<c>&lt;root&gt;/_master</c>).</summary>
        public static string MasterDir => Path.Combine(RootDir, "_master");

        /// <summary>Raw per-day backup copies of received CSVs (<c>&lt;root&gt;/_backup</c>).</summary>
        public static string BackupDir => Path.Combine(RootDir, "_backup");

        /// <summary>External MySQL connection settings file (<c>&lt;root&gt;/mysql.xml</c>).</summary>
        public static string MySqlConfigPath => Path.Combine(RootDir, "mysql.xml");

        /// <summary>UI show/hide toggles file (<c>&lt;app&gt;/configuration.xml</c>).</summary>
        public static string AppConfigPath => Path.Combine(AppDir, "configuration.xml");

        /// <summary>Where the crash log and the persisted UI language are written (inside <c>app/</c>).</summary>
        public static string AppDataFile(string name) => Path.Combine(AppDir, name);
    }
}
