using System.IO;
using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Monitoring;
using LeontecSyncLogSystem.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Single-process entry point. Boots an in-process generic host that runs the
    /// background work (serial Bluetooth listeners, the Kestrel /api/sync Wi-Fi endpoint,
    /// and the EF Core database), then runs the WinForms monitoring dashboard on the UI
    /// thread. The dashboard reads state directly from the host's services — no HTTP
    /// round-trip to itself.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                // Resolve appsettings.json next to the executable regardless of CWD.
                ContentRootPath = AppContext.BaseDirectory,
            });

            // --- Options ------------------------------------------------------------
            builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
            builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
            var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                            ?? new DatabaseOptions();

            // --- Embedded MariaDB (bundled, self-contained) --------------------------
            // When enabled (default), start our own MariaDB shipped under <app>/mariadb/ so the app
            // needs no separate MySQL install. Must be up BEFORE the DbContext connects/migrates.
            EmbeddedMariaDbServer? embeddedDb = null;
            string connectionString = dbOptions.ConnectionString;
            if (dbOptions.Embedded)
            {
                var mariadbBase = Path.Combine(AppContext.BaseDirectory, "mariadb");
                var dataDir = string.IsNullOrWhiteSpace(dbOptions.DataDir)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LeontecSyncLogSystem", "db-data")
                    : dbOptions.DataDir;

                using var boot = LoggerFactory.Create(b => b.AddConsole());
                embeddedDb = new EmbeddedMariaDbServer(
                    mariadbBase, dataDir, dbOptions.EmbeddedPort, boot.CreateLogger("EmbeddedMariaDB"));
                embeddedDb.Start();                       // blocks until the server accepts connections
                connectionString = embeddedDb.ConnectionString;
                builder.Services.AddSingleton(embeddedDb); // keep alive; disposed on shutdown
            }

            // --- Database (MySQL/MariaDB via Pomelo) ---------------------------------
            builder.Services.AddDbContext<AppDbContext>(options =>
            {
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
                    .UseSnakeCaseNamingConvention();
            });

            // --- Application services ------------------------------------------------
            builder.Services.AddSingleton<ServiceStatus>();
            builder.Services.AddSingleton<IDeviceStore, DeviceStore>();
            builder.Services.AddSingleton<ICsvStore, CsvStore>();

            // On-disk backup of every received CSV. Default folder = %LOCALAPPDATA%/LeontecSyncLogSystem/backup.
            var syncOptions = builder.Configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>()
                              ?? new SyncOptions();
            var backupRoot = string.IsNullOrWhiteSpace(syncOptions.BackupFolder)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LeontecSyncLogSystem", "backup")
                : syncOptions.BackupFolder;
            builder.Services.AddSingleton<ICsvBackupWriter>(sp =>
                new CsvBackupWriter(backupRoot, sp.GetRequiredService<ILogger<CsvBackupWriter>>()));

            // Editable master files (customer + item), source-of-truth on the PC. Default folder =
            // %LOCALAPPDATA%/LeontecSyncLogSystem/master; seeded from the bundled copy next to the exe.
            var masterRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LeontecSyncLogSystem", "master");
            var masterSeedRoot = Path.Combine(AppContext.BaseDirectory, "master-seed");
            builder.Services.AddSingleton<IMasterStore>(sp =>
                new MasterStore(masterRoot, masterSeedRoot, sp.GetRequiredService<ILogger<MasterStore>>()));

            builder.Services.AddSingleton<MonitorService>();
            builder.Services.AddHostedService<Worker>();

            var app = builder.Build();

            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Program")
                .LogInformation("CSV backup folder: {Root} (per-day subfolders, one file per upload).", backupRoot);

            // --- Apply EF Core migrations + load the persisted device roster ---------
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Migrations are the single source of truth for the MySQL schema (no more
                // hand-written CREATE TABLE / ALTER TABLE). Creates the DB if it doesn't exist
                // and applies any pending migrations.
                db.Database.Migrate();

                // Seed the in-memory roster so previously seen devices reappear (offline).
                var status = scope.ServiceProvider.GetRequiredService<ServiceStatus>();
                var deviceStore = scope.ServiceProvider.GetRequiredService<IDeviceStore>();
                var saved = deviceStore.LoadAllAsync().GetAwaiter().GetResult();
                status.SeedFromPersisted(saved);

                scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Startup")
                    .LogInformation("Loaded {Count} persisted device(s) from DB.", saved.Count);
            }

            // --- HTTP API ------------------------------------------------------------
            // Wi-Fi backup ingest is PARKED. The legacy CSV parser/ingest path was removed with the
            // SyncLogs table, so this endpoint currently accepts nothing. To re-enable Wi-Fi, wire up
            // the typed-CSV ingest here (map into CsvUploads via ICsvStore) and bind Kestrel on 8080.
            app.MapPost("/api/sync", () =>
                Results.Json(new { error = "Wi-Fi ingest is not enabled." }, statusCode: StatusCodes.Status501NotImplemented));

            // Remote monitoring (same data the local dashboard shows).
            app.MapGet("/api/status", async (MonitorService monitor, CancellationToken token) =>
                Results.Ok(await monitor.GetSnapshotAsync(token)));

            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

            // --- Start background host, then run the UI ------------------------------
            app.Start(); // non-blocking: Kestrel + serial listeners run on background threads

            ApplicationConfiguration.Initialize();

            // Crash safety net: a startup exception (e.g. a bad layout value baked into
            // InitializeComponent) would otherwise kill the process before the window shows, with
            // no clue why ("run rồi tắt liền"). Log every unhandled exception to a file and show it.
            Application.ThreadException += (_, e) => ReportFatal("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal("AppDomain", e.ExceptionObject as Exception);

            try
            {
                Application.Run(new MainForm(
                    app.Services.GetRequiredService<MonitorService>(),
                    app.Services.GetRequiredService<ICsvBackupWriter>(),
                    app.Services.GetRequiredService<IMasterStore>()));
            }
            catch (Exception ex)
            {
                ReportFatal("Startup", ex);
            }
            finally
            {
                // Stop the background host on a bounded timeout — a 32feet RFCOMM accept can hold a
                // thread that doesn't always unblock cleanly, so we never wait indefinitely.
                try
                {
                    app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    (app as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    // Best-effort: log and continue to the hard exit below so the process still dies.
                    Console.Error.WriteLine($"Host shutdown error (ignored): {ex.Message}");
                }

                // Stop the bundled MariaDB last (after the host released its DB connections).
                try { embeddedDb?.Dispose(); }
                catch (Exception ex) { Console.Error.WriteLine($"Embedded DB shutdown error (ignored): {ex.Message}"); }
            }

            // Guarantee the process actually terminates and releases LeontecSyncLogSystem.exe. If a
            // native Bluetooth/Kestrel thread we can't cancel were left alive, the orphaned process
            // would keep the .exe file-locked and block the next build's apphost→exe copy. A clean
            // exit here is what stops "the file is locked by LeontecSyncLogSystem (PID)" recurring.
            Environment.Exit(0);
        }

        /// <summary>
        /// Last-resort handler for an otherwise-unhandled exception: append it to a crash log under
        /// the user's local app-data and show it, so a startup failure is never a silent exit.
        /// </summary>
        private static void ReportFatal(string source, Exception? ex)
        {
            var message = ex?.ToString() ?? "(unknown error)";
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LeontecSyncLogSystem");
                Directory.CreateDirectory(dir);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} unhandled exception:{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(dir, "crash.log"), line);
            }
            catch { /* logging must never throw from the crash handler */ }

            try
            {
                MessageBox.Show(
                    $"{source} error — the app must close.{Environment.NewLine}{Environment.NewLine}{message}",
                    "Leontec Sync Monitor — fatal error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* no UI available */ }
        }
    }
}
