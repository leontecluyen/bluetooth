using System.Diagnostics;
using System.Net.Sockets;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Runs a MariaDB server bundled with the app as a managed child process, so the app is
    /// self-contained: copy it to another PC and the database "just works" — no separate MySQL
    /// install. The server binaries ship under <c>&lt;app&gt;/mariadb/</c>; the data lives in a
    /// writable per-user folder. First run initializes the data directory; the process is shut down
    /// gracefully on <see cref="Dispose"/>.
    ///
    /// Listens on 127.0.0.1 only, on a private port (default 3307) so it never clashes with any
    /// MySQL already installed on 3306. Root has an empty password (localhost-only).
    /// </summary>
    public sealed class EmbeddedMariaDbServer : IDisposable
    {
        private const string DatabaseName = "leontec_sync";

        private readonly string _baseDir;   // mariadb root (contains bin/ and share/)
        private readonly string _binDir;    // mariadb/bin
        private readonly string _dataDir;   // writable data directory
        private readonly int _port;
        private readonly ILogger _logger;
        private Process? _proc;

        public EmbeddedMariaDbServer(string mariadbBaseDir, string dataDir, int port, ILogger logger)
        {
            _baseDir = mariadbBaseDir;
            _binDir = Path.Combine(mariadbBaseDir, "bin");
            _dataDir = dataDir;
            _port = port;
            _logger = logger;
        }

        /// <summary>Connection string the app should use to reach this embedded server.</summary>
        public string ConnectionString =>
            $"Server=127.0.0.1;Port={_port};Database={DatabaseName};User=root;Password=;" +
            "AllowUserVariables=true;Connection Timeout=30;";

        /// <summary>The default MySQL/MariaDB port for this server (informational).</summary>
        public int Port => _port;

        /// <summary>
        /// Initialize (first run only), launch mysqld, and block until it accepts connections.
        /// Throws if the bundled binaries are missing or the server never becomes ready.
        /// </summary>
        public void Start()
        {
            var mysqld = ResolveExe("mysqld", "mariadbd");
            if (mysqld is null)
                throw new FileNotFoundException(
                    $"Bundled MariaDB not found under '{_binDir}'. Ensure the 'mariadb' folder ships with the app.");

            // First run: create the system tables in an empty data directory.
            bool initialized = Directory.Exists(Path.Combine(_dataDir, "mysql"));
            if (!initialized)
            {
                Directory.CreateDirectory(_dataDir);
                var installDb = ResolveExe("mariadb-install-db", "mysql_install_db");
                if (installDb is null)
                    throw new FileNotFoundException("mariadb-install-db(.exe) not found in the bundled MariaDB bin folder.");

                _logger.LogInformation("Initializing embedded MariaDB data directory: {Dir}", _dataDir);
                // The Windows mysql_install_db.exe has its OWN limited option set (it derives basedir
                // from its location; --no-defaults/--basedir/--auth-root-* are NOT accepted). It creates
                // root with no password; --allow-remote-root-access widens root to any host so a
                // 127.0.0.1 TCP login works (mysqld still binds to loopback only).
                RunToCompletion(installDb,
                    $"--datadir=\"{_dataDir}\" --port={_port} --allow-remote-root-access",
                    TimeSpan.FromMinutes(2));
            }

            _logger.LogInformation("Starting embedded MariaDB (port {Port}, data {Dir})…", _port, _dataDir);
            var psi = new ProcessStartInfo
            {
                FileName = mysqld,
                Arguments =
                    $"--no-defaults --basedir=\"{_baseDir}\" --datadir=\"{_dataDir}\" " +
                    $"--port={_port} --bind-address=127.0.0.1 --skip-name-resolve",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _binDir,
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) _logger.LogDebug("[mariadb] {Line}", e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _logger.LogDebug("[mariadb] {Line}", e.Data); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            WaitUntilAcceptingConnections(TimeSpan.FromSeconds(60));
            _logger.LogInformation("Embedded MariaDB is ready on 127.0.0.1:{Port}.", _port);
        }

        /// <summary>Polls the TCP port until the server accepts a connection (or times out).</summary>
        private void WaitUntilAcceptingConnections(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_proc is { HasExited: true })
                    throw new InvalidOperationException(
                        $"Embedded MariaDB exited during startup (code {_proc.ExitCode}). See [mariadb] debug logs.");
                try
                {
                    using var client = new TcpClient();
                    var connect = client.BeginConnect("127.0.0.1", _port, null, null);
                    if (connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)) && client.Connected)
                    {
                        client.EndConnect(connect);
                        return;
                    }
                }
                catch { /* not up yet */ }
                Thread.Sleep(300);
            }
            throw new TimeoutException($"Embedded MariaDB did not accept connections within {timeout.TotalSeconds:F0}s.");
        }

        public void Dispose()
        {
            if (_proc is null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    // Graceful shutdown first (flushes + closes cleanly).
                    var admin = ResolveExe("mariadb-admin", "mysqladmin");
                    if (admin != null)
                    {
                        _logger.LogInformation("Shutting down embedded MariaDB…");
                        RunToCompletion(admin,
                            $"--no-defaults --host=127.0.0.1 --port={_port} -u root shutdown",
                            TimeSpan.FromSeconds(15), throwOnError: false);
                    }
                    if (!_proc.WaitForExit(12000) && !_proc.HasExited)
                    {
                        _logger.LogWarning("Embedded MariaDB did not stop gracefully; killing the process.");
                        _proc.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error while stopping embedded MariaDB (ignored): {Msg}", ex.Message);
            }
            finally
            {
                _proc.Dispose();
                _proc = null;
            }
        }

        /// <summary>Returns the full path to the first of the given exe base-names that exists in bin.</summary>
        private string? ResolveExe(params string[] baseNames)
        {
            foreach (var name in baseNames)
            {
                var p = Path.Combine(_binDir, name + ".exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void RunToCompletion(string exe, string args, TimeSpan timeout, bool throwOnError = true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _binDir,
            };
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) _logger.LogDebug("[{Exe}] {Line}", Path.GetFileName(exe), e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) _logger.LogDebug("[{Exe}] {Line}", Path.GetFileName(exe), e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                if (throwOnError) throw new TimeoutException($"{Path.GetFileName(exe)} timed out after {timeout.TotalSeconds:F0}s.");
                return;
            }
            if (p.ExitCode != 0 && throwOnError)
                throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}). See debug logs.");
        }
    }
}
