using System.IO;
using System.Xml.Linq;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// External MySQL connection settings, read from <c>mysql.xml</c> (see
    /// <see cref="AppPaths.MySqlConfigPath"/>). The MySQL server itself is installed and run
    /// SEPARATELY (its own installer / Windows service) — this app does NOT bundle or start a DB
    /// anymore. It only reads these settings, connects, and reports whether the connection is alive.
    ///
    /// <para>File shape:</para>
    /// <code>
    /// &lt;mysql&gt;
    ///   &lt;host&gt;localhost&lt;/host&gt;
    ///   &lt;port&gt;3306&lt;/port&gt;
    ///   &lt;database&gt;log_management&lt;/database&gt;
    ///   &lt;user&gt;root&lt;/user&gt;
    ///   &lt;password&gt;&lt;/password&gt;
    /// &lt;/mysql&gt;
    /// </code>
    /// A missing file is created with these defaults so the operator has something to edit.
    /// </summary>
    public sealed class MySqlConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "log_management";
        public string User { get; set; } = "root";
        public string Password { get; set; } = "";

        /// <summary>Pomelo/MySQL connection string built from the settings above.</summary>
        public string BuildConnectionString() =>
            $"Server={Host};Port={Port};Database={Database};User={User};Password={Password};" +
            "AllowUserVariables=true;Connection Timeout=30;";

        /// <summary>Short "host:port/db" tag for status labels and logs (never includes the password).</summary>
        public string Endpoint => $"{Host}:{Port}/{Database}";

        /// <summary>
        /// Load the settings from <paramref name="path"/>; if the file is missing, write a default one
        /// and return the defaults. A parse error is logged and falls back to defaults (never throws),
        /// so a bad config can't stop the app from starting and showing "MySQL: disconnected".
        /// </summary>
        public static MySqlConfig LoadOrCreate(string path, ILogger logger)
        {
            try
            {
                if (!File.Exists(path))
                {
                    var created = new MySqlConfig();
                    created.Save(path);
                    logger.LogWarning(
                        "MySQL config not found — created default {Path} ({Endpoint}). Edit it to match your MySQL install.",
                        path, created.Endpoint);
                    return created;
                }

                var root = XDocument.Load(path).Root;
                var cfg = new MySqlConfig
                {
                    Host = Read(root, "host", "localhost"),
                    Port = int.TryParse(Read(root, "port", "3306"), out var p) ? p : 3306,
                    Database = Read(root, "database", "log_management"),
                    User = Read(root, "user", "root"),
                    Password = Read(root, "password", ""),
                };
                logger.LogInformation("Loaded MySQL config from {Path} ({Endpoint}, user {User}).",
                    path, cfg.Endpoint, cfg.User);
                return cfg;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read MySQL config {Path}; using defaults ({Endpoint}).",
                    path, new MySqlConfig().Endpoint);
                return new MySqlConfig();
            }
        }

        /// <summary>Write the settings to <paramref name="path"/> (creating the folder if needed).</summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            new XDocument(new XElement("mysql",
                new XElement("host", Host),
                new XElement("port", Port),
                new XElement("database", Database),
                new XElement("user", User),
                new XElement("password", Password)))
                .Save(path);
        }

        private static string Read(XElement? root, string name, string fallback)
        {
            var e = root?.Element(name);
            return e is null ? fallback : e.Value.Trim();
        }
    }
}
