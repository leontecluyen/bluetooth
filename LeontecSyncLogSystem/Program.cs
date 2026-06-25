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

            // --- Database (provider switchable via Database:Provider) ----------------
            builder.Services.AddDbContext<AppDbContext>(options =>
            {
                switch (dbOptions.Provider.Trim().ToLowerInvariant())
                {
                    case "sqlserver":
                        options.UseSqlServer(dbOptions.ConnectionString);
                        break;
                    case "postgres":
                    case "postgresql":
                        options.UseNpgsql(dbOptions.ConnectionString);
                        break;
                    default: // sqlite
                        options.UseSqlite(dbOptions.ConnectionString);
                        break;
                }
            });

            // --- Application services ------------------------------------------------
            builder.Services.AddSingleton<ServiceStatus>();
            builder.Services.AddSingleton<ILogIngestService, LogIngestService>();
            builder.Services.AddSingleton<IDeviceStore, DeviceStore>();
            builder.Services.AddSingleton<ICsvStore, CsvStore>();
            builder.Services.AddSingleton<MonitorService>();
            builder.Services.AddHostedService<Worker>();

            var app = builder.Build();

            // --- Ensure the schema exists + load the persisted device roster ---------
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();

                // EnsureCreated() does NOT add new tables to a pre-existing DB. The Devices
                // table was added later, so create it if missing (SQLite, non-destructive —
                // keeps existing logs). Other providers get it on a fresh EnsureCreated.
                if (dbOptions.Provider.Trim().ToLowerInvariant() is "sqlite" or "")
                {
                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "Devices" (
                            "Address" TEXT NOT NULL CONSTRAINT "PK_Devices" PRIMARY KEY,
                            "Name" TEXT NOT NULL,
                            "WorkerId" TEXT NULL,
                            "FirstSeenUtc" TEXT NULL,
                            "LastSeenUtc" TEXT NULL,
                            "LastFrameUtc" TEXT NULL,
                            "LastHeartbeatUtc" TEXT NULL,
                            "FramesReceived" INTEGER NOT NULL,
                            "RecordsIngested" INTEGER NOT NULL,
                            "Sessions" INTEGER NOT NULL,
                            "Heartbeats" INTEGER NOT NULL
                        );
                        """);

                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "CsvUploads" (
                            "Id" TEXT NOT NULL CONSTRAINT "PK_CsvUploads" PRIMARY KEY,
                            "DeviceAddress" TEXT NOT NULL,
                            "ReceivedAtUtc" TEXT NOT NULL,
                            "Source" TEXT NOT NULL,
                            "Device" TEXT NOT NULL,
                            "WorkerId" TEXT NULL,
                            "Type" TEXT NOT NULL DEFAULT 'unknown',
                            "TermId" TEXT NOT NULL DEFAULT '',
                            "UploadIndex" INTEGER NOT NULL DEFAULT 0,
                            "LogDate" TEXT NULL,
                            "Superseded" INTEGER NOT NULL DEFAULT 0,
                            "RowCount" INTEGER NOT NULL,
                            "Inserted" INTEGER NOT NULL,
                            "Duplicates" INTEGER NOT NULL,
                            "RawCsv" TEXT NOT NULL,
                            CONSTRAINT "FK_CsvUploads_Devices_DeviceAddress" FOREIGN KEY ("DeviceAddress")
                                REFERENCES "Devices" ("Address") ON DELETE CASCADE
                        );
                        """);
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_CsvUploads_DeviceAddress\" ON \"CsvUploads\" (\"DeviceAddress\");");

                    // Add the envelope columns to a pre-existing CsvUploads (ignore "duplicate column").
                    foreach (var col in new[]
                    {
                        "ALTER TABLE \"CsvUploads\" ADD COLUMN \"Type\" TEXT NOT NULL DEFAULT 'unknown';",
                        "ALTER TABLE \"CsvUploads\" ADD COLUMN \"TermId\" TEXT NOT NULL DEFAULT '';",
                        "ALTER TABLE \"CsvUploads\" ADD COLUMN \"UploadIndex\" INTEGER NOT NULL DEFAULT 0;",
                        "ALTER TABLE \"CsvUploads\" ADD COLUMN \"Superseded\" INTEGER NOT NULL DEFAULT 0;",
                        // LogDate added later (per-day log filter); nullable so old rows stay valid.
                        "ALTER TABLE \"CsvUploads\" ADD COLUMN \"LogDate\" TEXT NULL;",
                    })
                    {
                        try { db.Database.ExecuteSqlRaw(col); } catch { /* column already exists */ }
                    }
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_CsvUploads_Type_LogDate\" ON \"CsvUploads\" (\"Type\", \"LogDate\");");

                    // Normalized per-type tables (each row of a typed CSV).
                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "MonitorEntries" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_MonitorEntries" PRIMARY KEY AUTOINCREMENT,
                            "UploadId" TEXT NOT NULL,
                            "StartTime" TEXT NOT NULL, "EndTime" TEXT NOT NULL,
                            "SlipNo" TEXT NOT NULL, "CustomerCode" TEXT NOT NULL, "ItemCode" TEXT NOT NULL,
                            "Boxes" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, "LoadedBoxes" INTEGER NOT NULL,
                            "Status" TEXT NOT NULL, "StatusCode" TEXT NOT NULL DEFAULT '',
                            CONSTRAINT "FK_MonitorEntries_CsvUploads" FOREIGN KEY ("UploadId")
                                REFERENCES "CsvUploads" ("Id") ON DELETE CASCADE
                        );
                        """);
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_MonitorEntries_UploadId\" ON \"MonitorEntries\" (\"UploadId\");");

                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "PalletOps" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_PalletOps" PRIMARY KEY AUTOINCREMENT,
                            "UploadId" TEXT NOT NULL,
                            "OpType" TEXT NOT NULL, "StartTime" TEXT NOT NULL, "EndTime" TEXT NOT NULL,
                            "PlNo" TEXT NOT NULL, "Customer" TEXT NOT NULL, "DeliveryRun" TEXT NOT NULL,
                            "ItemDetailRaw" TEXT NOT NULL, "StatusCode" TEXT NOT NULL DEFAULT '',
                            CONSTRAINT "FK_PalletOps_CsvUploads" FOREIGN KEY ("UploadId")
                                REFERENCES "CsvUploads" ("Id") ON DELETE CASCADE
                        );
                        """);
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_PalletOps_UploadId\" ON \"PalletOps\" (\"UploadId\");");

                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "PalletOpItems" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_PalletOpItems" PRIMARY KEY AUTOINCREMENT,
                            "PalletOpId" INTEGER NOT NULL,
                            "ItemCode" TEXT NOT NULL, "Boxes" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL,
                            CONSTRAINT "FK_PalletOpItems_PalletOps" FOREIGN KEY ("PalletOpId")
                                REFERENCES "PalletOps" ("Id") ON DELETE CASCADE
                        );
                        """);
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_PalletOpItems_PalletOpId\" ON \"PalletOpItems\" (\"PalletOpId\");");

                    // Direct-delivery (直送管理) normalized rows.
                    db.Database.ExecuteSqlRaw(
                        """
                        CREATE TABLE IF NOT EXISTS "DirectEntries" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_DirectEntries" PRIMARY KEY AUTOINCREMENT,
                            "UploadId" TEXT NOT NULL,
                            "StartTime" TEXT NOT NULL, "EndTime" TEXT NOT NULL,
                            "Customer" TEXT NOT NULL, "DeliveryTo" TEXT NOT NULL, "ShipDate" TEXT NOT NULL,
                            "PartNo" TEXT NOT NULL, "Capacity" INTEGER NOT NULL, "Boxes" INTEGER NOT NULL,
                            "DeliveryQty" INTEGER NOT NULL, "FactoryCode" TEXT NOT NULL, "YokooPartNo" TEXT NOT NULL,
                            CONSTRAINT "FK_DirectEntries_CsvUploads" FOREIGN KEY ("UploadId")
                                REFERENCES "CsvUploads" ("Id") ON DELETE CASCADE
                        );
                        """);
                    db.Database.ExecuteSqlRaw(
                        "CREATE INDEX IF NOT EXISTS \"IX_DirectEntries_UploadId\" ON \"DirectEntries\" (\"UploadId\");");

                    // Add StatusCode (状態 code) to typed tables created by an earlier build.
                    foreach (var col in new[]
                    {
                        "ALTER TABLE \"MonitorEntries\" ADD COLUMN \"StatusCode\" TEXT NOT NULL DEFAULT '';",
                        "ALTER TABLE \"PalletOps\" ADD COLUMN \"StatusCode\" TEXT NOT NULL DEFAULT '';",
                    })
                    {
                        try { db.Database.ExecuteSqlRaw(col); } catch { /* column already exists */ }
                    }
                }

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
            // Wi-Fi backup: Android uploads a CSV document here.
            app.MapPost("/api/sync", async (HttpRequest request, ILogIngestService ingest, CancellationToken token) =>
            {
                using var reader = new StreamReader(request.Body);
                var csv = await reader.ReadToEndAsync(token);

                if (string.IsNullOrWhiteSpace(csv))
                    return Results.BadRequest(new { error = "Empty body. Expected CSV." });

                var entries = CsvLogParser.ParseDocument(csv, "WiFi");
                if (entries.Count == 0)
                    return Results.BadRequest(new { error = "No valid CSV rows could be parsed." });

                var result = await ingest.IngestAsync(entries, token);
                return Results.Ok(new { received = result.Received, inserted = result.Inserted, duplicates = result.Duplicates });
            });

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
                Application.Run(new MainForm(app.Services.GetRequiredService<MonitorService>()));
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
