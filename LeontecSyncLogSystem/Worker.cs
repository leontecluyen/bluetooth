using Microsoft.Extensions.Options;
using LeontecSyncLogSystem.Services;

namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Primary ingestion layer. Runs the Bluetooth SPP server that accepts connections from
    /// multiple Android devices concurrently. Self-heals if the radio is off/absent.
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly ILogIngestService _ingest;
        private readonly ServiceStatus _status;
        private readonly ICsvStore _csvStore;
        private readonly IDeviceStore _deviceStore;
        private readonly SyncOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<Worker> _logger;

        public Worker(
            ILogIngestService ingest,
            ServiceStatus status,
            ICsvStore csvStore,
            IDeviceStore deviceStore,
            IOptions<SyncOptions> options,
            ILoggerFactory loggerFactory,
            ILogger<Worker> logger)
        {
            _ingest = ingest;
            _status = status;
            _csvStore = csvStore;
            _deviceStore = deviceStore;
            _options = options.Value;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker starting Bluetooth SPP server…");

            var server = new BluetoothSppServer(
                _ingest,
                _status,
                _csvStore,
                _deviceStore,
                _options.BluetoothServiceName,
                _loggerFactory.CreateLogger<BluetoothSppServer>());

            await server.RunAsync(stoppingToken);
        }
    }
}
