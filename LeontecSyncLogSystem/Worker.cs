using LeontecSyncLogSystem.Services;

namespace LeontecSyncLogSystem
{
    /// <summary>
    /// Primary ingestion layer. Runs the Bluetooth SPP server that accepts connections from
    /// multiple Android devices concurrently. Self-heals if the radio is off/absent.
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly BluetoothSppServer _server;
        private readonly ILogger<Worker> _logger;

        public Worker(BluetoothSppServer server, ILogger<Worker> logger)
        {
            _server = server;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker starting Bluetooth SPP server…");
            await _server.RunAsync(stoppingToken);
        }
    }
}
