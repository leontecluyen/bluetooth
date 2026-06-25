using LeontecSyncLogSystem.Data;
using LeontecSyncLogSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Persists the Bluetooth device roster (and per-device counters/timestamps) to the DB so
    /// it survives app restarts. The live <see cref="ServiceStatus"/> is the working copy;
    /// this just mirrors each device's last-known state into the <c>Devices</c> table.
    /// </summary>
    public interface IDeviceStore
    {
        Task<IReadOnlyList<DeviceRecord>> LoadAllAsync(CancellationToken token = default);
        Task UpsertAsync(BtClientStatus client, CancellationToken token = default);
        Task ClearAsync(CancellationToken token = default);
    }

    public class DeviceStore : IDeviceStore
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DeviceStore> _logger;

        public DeviceStore(IServiceScopeFactory scopeFactory, ILogger<DeviceStore> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<IReadOnlyList<DeviceRecord>> LoadAllAsync(CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Devices.AsNoTracking().ToListAsync(token);
        }

        /// <summary>Insert or update one device's persisted state from its live status.</summary>
        public async Task UpsertAsync(BtClientStatus client, CancellationToken token = default)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var rec = await db.Devices.FirstOrDefaultAsync(d => d.Address == client.Address, token);
                if (rec is null)
                {
                    rec = new DeviceRecord { Address = client.Address, FirstSeenUtc = DateTime.UtcNow };
                    db.Devices.Add(rec);
                }

                rec.Name = client.Name;
                rec.WorkerId = client.WorkerId;
                rec.LastSeenUtc = client.LastSeenUtc;
                rec.LastFrameUtc = client.LastFrameUtc;
                rec.LastHeartbeatUtc = client.LastHeartbeatUtc;
                rec.FramesReceived = client.FramesReceived;
                rec.RecordsIngested = client.RecordsIngested;
                rec.Sessions = client.Sessions;
                rec.Heartbeats = client.Heartbeats;

                await db.SaveChangesAsync(token);
            }
            catch (Exception ex)
            {
                // Persistence is best-effort — never let it break ingestion/heartbeat handling.
                _logger.LogWarning("Failed to persist device {Addr}: {Msg}", client.Address, ex.Message);
            }
        }

        public async Task ClearAsync(CancellationToken token = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Devices.ExecuteDeleteAsync(token);
        }
    }
}
