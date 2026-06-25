using System.Collections.Concurrent;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Live status of one Bluetooth client = one Android device that has connected
    /// (or is connected) to the SPP server. Keyed by Bluetooth address so reconnects
    /// of the same phone update the same row.
    /// </summary>
    public sealed class BtClientStatus
    {
        public string Address { get; }
        public volatile string Name;
        private volatile string? _workerId;
        public volatile bool Connected;
        public DateTime? ConnectedAtUtc;
        /// <summary>Last <b>data</b> (CSV) frame received.</summary>
        public DateTime? LastFrameUtc;
        /// <summary>Last contact of <b>any</b> kind (data frame or heartbeat) — drives Online/Offline.</summary>
        public DateTime? LastSeenUtc;
        /// <summary>Last heartbeat ping received from the device.</summary>
        public DateTime? LastHeartbeatUtc;

        private long _framesReceived;
        private long _recordsIngested;
        private long _sessions;
        private long _heartbeats;

        public BtClientStatus(string address, string name)
        {
            Address = address;
            Name = name;
        }

        /// <summary>WorkerId reported in the device's CSV (null until first frame).</summary>
        public string? WorkerId => _workerId;
        public long FramesReceived => Interlocked.Read(ref _framesReceived);
        public long RecordsIngested => Interlocked.Read(ref _recordsIngested);
        /// <summary>Number of data (CSV) sync sessions — heartbeat-only connections don't count.</summary>
        public long Sessions => Interlocked.Read(ref _sessions);
        /// <summary>Total heartbeat pings received from this device.</summary>
        public long Heartbeats => Interlocked.Read(ref _heartbeats);

        /// <summary>
        /// True if the device is considered present: currently connected, or its last contact
        /// (data or heartbeat) was within <see cref="ServiceStatus.HeartbeatTimeout"/>.
        /// </summary>
        public bool IsOnline(DateTime nowUtc) =>
            Connected || (LastSeenUtc is DateTime seen && nowUtc - seen <= ServiceStatus.HeartbeatTimeout);

        public void MarkConnected()
        {
            Connected = true;
            ConnectedAtUtc = DateTime.UtcNow;
            Touch();
        }

        public void MarkDisconnected() => Connected = false;

        public void SetDevice(string workerId)
        {
            if (!string.IsNullOrWhiteSpace(workerId))
                _workerId = workerId;
        }

        public void AddFrame()
        {
            Interlocked.Increment(ref _framesReceived);
            LastFrameUtc = DateTime.UtcNow;
            Touch();
        }

        /// <summary>Records a heartbeat ping (keeps the device "online" without a data sync).</summary>
        public void AddHeartbeat()
        {
            Interlocked.Increment(ref _heartbeats);
            LastHeartbeatUtc = DateTime.UtcNow;
            Touch();
        }

        /// <summary>Counts one completed data (CSV) sync session.</summary>
        public void AddDataSession() => Interlocked.Increment(ref _sessions);

        public void AddRecords(int count) => Interlocked.Add(ref _recordsIngested, count);

        private void Touch() => LastSeenUtc = DateTime.UtcNow;

        /// <summary>
        /// Restore persisted state when the app reloads the device roster from the DB. The
        /// device starts <b>offline</b> (Connected=false) until it reconnects.
        /// </summary>
        public void RestoreFrom(
            string? workerId, DateTime? lastSeen, DateTime? lastFrame, DateTime? lastHeartbeat,
            long frames, long records, long sessions, long heartbeats)
        {
            _workerId = workerId;
            LastSeenUtc = lastSeen;
            LastFrameUtc = lastFrame;
            LastHeartbeatUtc = lastHeartbeat;
            Interlocked.Exchange(ref _framesReceived, frames);
            Interlocked.Exchange(ref _recordsIngested, records);
            Interlocked.Exchange(ref _sessions, sessions);
            Interlocked.Exchange(ref _heartbeats, heartbeats);
            Connected = false;
        }
    }

    /// <summary>
    /// Process-wide, thread-safe runtime state. Registered as a singleton; the Bluetooth
    /// SPP server pushes updates and the dashboard / api reads it.
    /// </summary>
    public sealed class ServiceStatus
    {
        /// <summary>
        /// A device is considered offline once it has not contacted the PC (data frame or
        /// heartbeat) within this window. Matches the Android heartbeat policy: ping every 5 s,
        /// declare offline after 3 missed pings (15 s).
        /// </summary>
        public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

        private readonly ConcurrentDictionary<string, BtClientStatus> _clients =
            new(StringComparer.OrdinalIgnoreCase);

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        // --- Bluetooth SPP server state ---
        public volatile bool ServerListening;
        /// <summary>The PC's Bluetooth radio name — the name the phone must target.</summary>
        public volatile string? RadioName;
        public volatile string? ServiceName;
        public volatile string? LastError;

        public BtClientStatus GetOrAddClient(string address, string name)
        {
            var c = _clients.GetOrAdd(address, a => new BtClientStatus(a, name));
            if (!string.IsNullOrWhiteSpace(name) && name != address)
                c.Name = name;
            return c;
        }

        public IReadOnlyList<BtClientStatus> Clients
        {
            get
            {
                var now = DateTime.UtcNow;
                return _clients.Values
                    .OrderByDescending(c => c.IsOnline(now))
                    .ThenByDescending(c => c.LastSeenUtc ?? DateTime.MinValue)
                    .ToList();
            }
        }

        /// <summary>
        /// Forget all known clients (used by "Clear all DB"). Currently-connected devices
        /// reappear on their next frame; this just clears the historical/disconnected rows
        /// and resets counters.
        /// </summary>
        public void ClearClients() => _clients.Clear();

        /// <summary>
        /// Seed the in-memory roster from persisted device records on startup, so previously
        /// seen devices show up (offline) instead of vanishing when the app restarts.
        /// </summary>
        public void SeedFromPersisted(IEnumerable<Models.DeviceRecord> records)
        {
            foreach (var r in records)
            {
                var c = GetOrAddClient(r.Address, string.IsNullOrWhiteSpace(r.Name) ? r.Address : r.Name);
                c.RestoreFrom(r.WorkerId, r.LastSeenUtc, r.LastFrameUtc, r.LastHeartbeatUtc,
                    r.FramesReceived, r.RecordsIngested, r.Sessions, r.Heartbeats);
            }
        }
    }
}
