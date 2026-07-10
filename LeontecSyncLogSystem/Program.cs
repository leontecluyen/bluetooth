using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Monitoring;
using LeontecSyncLogSystem.Services;
using LeontecSyncLogSystem.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Single-process entry point (.NET Framework 4.8). Boots an in-process generic host that runs the
    /// background work (Bluetooth SPP server via <see cref="Worker"/>, the HttpListener monitoring API,
    /// and the EF Core database), then runs the WinForms dashboard on the UI thread. The dashboard
    /// reads state directly from the host's services — no HTTP round-trip to itself.
    ///
    /// <para>Kestrel/ASP.NET Core aren't available on net48, so the HTTP API uses
    /// <see cref="System.Net.HttpListener"/> (<see cref="HttpApiService"/>) and the app uses the
    /// generic <see cref="Host"/> instead of <c>WebApplication</c>.</para>
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using var boot = LoggerFactory.Create(b => b.AddConsole());

            // Load external config files up-front (needed to build the connection string + register
            // singletons). MySQL is EXTERNAL; a missing mysql.xml is created with defaults.
            var mySql = MySqlConfig.LoadOrCreate(AppPaths.MySqlConfigPath, boot.CreateLogger("MySqlConfig"));
            var uiConfig = UiConfig.LoadOrCreate(AppPaths.AppConfigPath, boot.CreateLogger("UiConfig"));

            var host = Host.CreateDefaultBuilder(args)
                // Resolve appsettings.json next to the executable regardless of the current directory.
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureServices((ctx, services) =>
                {
                    // --- Options ----------------------------------------------------
                    services.Configure<SyncOptions>(ctx.Configuration.GetSection(SyncOptions.SectionName));

                    // --- Database (EXTERNAL MySQL, connection from mysql.xml) --------
                    services.AddSingleton(mySql);
                    var connectionString = mySql.BuildConnectionString();
                    services.AddDbContext<AppDbContext>(options =>
                    {
                        // Pomelo 3.2 (EF Core 3.1) takes only the connection string — no ServerVersion
                        // arg and no connection opened at configuration time, so the context builds
                        // offline and a momentarily-down MySQL doesn't stop startup.
                        options.UseMySql(connectionString).UseSnakeCaseNamingConvention();
                    });

                    // --- UI show/hide toggles (configuration.xml, all default hidden) ---
                    services.AddSingleton(uiConfig);

                    // --- Application services ----------------------------------------
                    services.AddSingleton<ServiceStatus>();
                    services.AddSingleton<IDeviceStore, DeviceStore>();
                    services.AddSingleton<ICsvStore, CsvStore>();

                    // On-disk backup of every received CSV. Folder = <root>/_backup unless
                    // Sync:BackupFolder overrides it with an absolute path.
                    var syncOptions = ctx.Configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>()
                                      ?? new SyncOptions();
                    var backupRoot = string.IsNullOrWhiteSpace(syncOptions.BackupFolder)
                        ? AppPaths.BackupDir
                        : syncOptions.BackupFolder;
                    services.AddSingleton<ICsvBackupWriter>(sp =>
                        new CsvBackupWriter(backupRoot, sp.GetRequiredService<ILogger<CsvBackupWriter>>()));

                    // Editable master files (customer + item), source-of-truth on the PC.
                    var masterRoot = AppPaths.MasterDir;
                    var masterSeedRoot = Path.Combine(AppContext.BaseDirectory, "master-seed");
                    services.AddSingleton<IMasterStore>(sp =>
                        new MasterStore(masterRoot, masterSeedRoot, sp.GetRequiredService<ILogger<MasterStore>>()));

                    // The Bluetooth SPP server is a singleton so BOTH the Worker (accept loop) and the
                    // dashboard (master push) share one instance + its live-connection registry.
                    services.AddSingleton<BluetoothSppServer>(sp => new BluetoothSppServer(
                        sp.GetRequiredService<ServiceStatus>(),
                        sp.GetRequiredService<ICsvStore>(),
                        sp.GetRequiredService<IDeviceStore>(),
                        sp.GetRequiredService<ICsvBackupWriter>(),
                        sp.GetRequiredService<IMasterStore>(),
                        syncOptions.BluetoothServiceName,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<BluetoothSppServer>()));

                    services.AddSingleton<MonitorService>();
                    services.AddHostedService<Worker>();
                    // HTTP monitoring API (HttpListener replaces Kestrel on net48).
                    services.AddHostedService<HttpApiService>();
                })
                .Build();

            var programLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
            var backupWriter = host.Services.GetRequiredService<ICsvBackupWriter>();
            programLog.LogInformation("CSV backup folder: {Root} (per-day subfolders, one file per upload).", backupWriter.Root);

            // --- Create/verify the DB schema + load the persisted device roster -----
            // MySQL is EXTERNAL, so it may be down at startup. We must NOT crash — the dashboard still
            // opens and shows "MySQL: disconnected"; the schema/roster load on a later restart.
            using (var scope = host.Services.CreateScope())
            {
                var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // EF Core 3.1 has no ExecuteUpdate/migrations tooling on net48 here; EnsureCreated
                    // creates the database + schema from the model (incl. the DateOnly/TimeOnly value
                    // converters) if they don't already exist.
                    db.Database.EnsureCreated();

                    var status = scope.ServiceProvider.GetRequiredService<ServiceStatus>();
                    var deviceStore = scope.ServiceProvider.GetRequiredService<IDeviceStore>();
                    var saved = deviceStore.LoadAllAsync().GetAwaiter().GetResult();
                    status.SeedFromPersisted(saved);

                    startupLog.LogInformation("Loaded {Count} persisted device(s) from DB.", saved.Count);
                }
                catch (Exception ex)
                {
                    startupLog.LogError(ex,
                        "Database not ready at startup ({Endpoint}). The app will start anyway and show " +
                        "'MySQL: disconnected'; schema/roster will load on a later restart once MySQL is up.",
                        mySql.Endpoint);
                }
            }

            // --- Start background host, then run the UI -----------------------------
            host.Start(); // non-blocking: hosted services (Worker + HttpListener) run on background threads

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Crash safety net: log every unhandled exception to a file and show it, so a startup
            // failure is never a silent exit ("run rồi tắt liền").
            Application.ThreadException += (_, e) => ReportFatal("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal("AppDomain", e.ExceptionObject as Exception);

            try
            {
                Application.Run(new MainForm(
                    host.Services.GetRequiredService<MonitorService>(),
                    host.Services.GetRequiredService<ICsvBackupWriter>(),
                    host.Services.GetRequiredService<IMasterStore>(),
                    host.Services.GetRequiredService<UiConfig>(),
                    host.Services.GetRequiredService<MySqlConfig>()));
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
                    host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Host shutdown error (ignored): {ex.Message}");
                }
            }

            // Guarantee the process actually terminates and releases the .exe (a lingering native
            // Bluetooth/HTTP thread would keep the file locked and block the next build).
            Environment.Exit(0);
        }

        /// <summary>
        /// Last-resort handler for an otherwise-unhandled exception: append it to crash.log (next to the
        /// exe) and show it, so a startup failure is never a silent exit.
        /// </summary>
        private static void ReportFatal(string source, Exception? ex)
        {
            var message = ex?.ToString() ?? "(unknown error)";
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} unhandled exception:{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(AppPaths.AppDataFile("crash.log"), line);
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
