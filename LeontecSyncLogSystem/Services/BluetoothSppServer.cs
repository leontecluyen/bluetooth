using System.Net.Sockets;
using System.Text;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Bluetooth SPP (RFCOMM) <b>server</b>. The Android app is the client: it finds this
    /// PC by Bluetooth name, connects to the SPP service UUID
    /// <c>00001101-0000-1000-8000-00805F9B34FB</c>, and streams a <c>STX + CSV + ETX</c>
    /// packet, then disconnects.
    ///
    /// Multiple devices are supported concurrently: the listener accepts clients in a loop
    /// and each connection is serviced on its own task with its own <see cref="FrameDecoder"/>.
    /// The accept/listen loop self-heals — a missing/off radio just retries every few seconds.
    /// </summary>
    public sealed class BluetoothSppServer
    {
        private static readonly Guid SppServiceUuid = new("00001101-0000-1000-8000-00805F9B34FB");
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        // Heartbeat (liveness) control frames. The phone opens a short SPP connection every
        // ~15 s and sends "PING,<deviceName>,<epochMillis>"; the PC records it and replies
        // "PONG,<radioName>,<epochMillis>" so the phone can show "listener OK". These never
        // carry log data, so they don't count as data frames/sessions.
        private const string HeartbeatPing = "PING";
        private const string HeartbeatPong = "PONG";

        // Batch result protocol. The phone sends all CSV frames on ONE connection, then a single
        // "BATCH_END" control frame. The PC accumulates each file's outcome and replies ONCE with a
        // framed summary: "RESULT\n<filename>=OK\n<filename>=ERR\n…" so the phone can move/delete
        // only the files that actually landed. Old phones that never send BATCH_END get no reply
        // (backward compatible — they just close the socket).
        private const string BatchEnd = "BATCH_END";
        private const string BatchResultHeader = "RESULT";

        // Master reverse-sync (PC → phone). The phone opens a connection and sends
        // "MASTER_REQ\n<name>=<pcMtimeMillis>\n…" — the value it stored the LAST time the PC sent that
        // file (a PC-clock timestamp; 0 if never). For each master whose PC file mtime is now GREATER
        // than that stored value the PC streams a file frame "STX + <name>\t<pcMtimeMillis>\r\n + <csv>
        // + ETX" (the phone stores that <pcMtimeMillis> and echoes it next time), then a closing frame
        // "MASTER_END\n<name>=UPDATED|UPTODATE\n…" reporting each master's status. Because BOTH sides
        // of the comparison are PC-clock values, phone/PC clock skew can't break it. Delivery is always
        // phone-initiated (the phone taps "receive master") — the PC never opens a connection to a
        // phone, so there's no push-from-PC. See docs/04.
        private const string MasterRequest = "MASTER_REQ";
        private const string MasterEnd = "MASTER_END";
        private const string MasterUpdated = "UPDATED";     // PC had a newer copy → the file was sent
        private const string MasterUpToDate = "UPTODATE";   // the phone's copy is already current

        private readonly ServiceStatus _status;
        private readonly ICsvStore _csvStore;
        private readonly IDeviceStore _deviceStore;
        private readonly ICsvBackupWriter _backup;
        private readonly IMasterStore _master;
        private readonly string _serviceName;
        private readonly ILogger _logger;

        public BluetoothSppServer(
            ServiceStatus status, ICsvStore csvStore, IDeviceStore deviceStore,
            ICsvBackupWriter backup, IMasterStore master, string serviceName, ILogger logger)
        {
            _status = status;
            _csvStore = csvStore;
            _deviceStore = deviceStore;
            _backup = backup;
            _master = master;
            _serviceName = serviceName;
            _logger = logger;
        }

        /// <summary>
        /// One live SPP connection: its stream + a lock that serializes writes to it. The reply
        /// writes (PONG / RESULT / master files) all happen on the connection's own read loop, so the
        /// lock is only defensive — but it keeps every write path uniform through
        /// <see cref="SendTextFrameAsync"/>.
        /// </summary>
        private sealed class ActiveConn
        {
            public required Stream Stream { get; init; }
            public required string Name { get; init; }
            public required string Address { get; init; }
            public SemaphoreSlim WriteLock { get; } = new(1, 1);
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                BluetoothListener? listener = null;
                try
                {
                    // Surface the PC radio name so the operator knows what the phone must target.
                    try
                    {
                        var radio = BluetoothRadio.Default;
                        _status.RadioName = radio?.Name;
                        _logger.LogInformation(
                            "Local Bluetooth radio: {Name} ({Addr}). The phone's pcBluetoothName must match {Name}.",
                            radio?.Name ?? "(none)", radio?.LocalAddress, radio?.Name ?? "(none)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not read local Bluetooth radio: {Msg}", ex.Message);
                    }

                    listener = new BluetoothListener(SppServiceUuid) { ServiceName = _serviceName };
                    listener.Start();

                    _status.ServerListening = true;
                    _status.ServiceName = _serviceName;
                    _status.LastError = null;
                    _logger.LogInformation(
                        "Bluetooth SPP server started (service '{Service}', UUID {Uuid}). Waiting for devices…",
                        _serviceName, SppServiceUuid);

                    // Stop the blocking Accept when shutdown is requested.
                    using var reg = token.Register(() => { try { listener.Stop(); } catch { /* ignore */ } });

                    while (!token.IsCancellationRequested)
                    {
                        BluetoothClient client;
                        try
                        {
                            client = await Task.Run(() => listener!.AcceptBluetoothClient(), CancellationToken.None);
                        }
                        catch (Exception) when (token.IsCancellationRequested)
                        {
                            break;
                        }

                        // Service each device concurrently — multiple phones at once.
                        _ = HandleClientAsync(client, token);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _status.ServerListening = false;
                    _status.LastError = ex.Message;
                    _logger.LogError(ex,
                        "Bluetooth listener error (is a Bluetooth radio present and on?). Retrying in {Delay}s.",
                        RetryDelay.TotalSeconds);
                    try { await Task.Delay(RetryDelay, token); } catch (OperationCanceledException) { break; }
                }
                finally
                {
                    _status.ServerListening = false;
                    try { listener?.Stop(); } catch { /* ignore */ }
                }
            }

            _logger.LogInformation("Bluetooth SPP server stopped.");
        }

        private async Task HandleClientAsync(BluetoothClient client, CancellationToken token)
        {
            var name = SafeName(client);
            var address = SafeAddress(client) ?? name;
            var cs = _status.GetOrAddClient(address, name);

            // A connection is opened for every CSV sync AND every 15 s heartbeat, so the raw
            // connect/disconnect is logged at Debug to avoid spam. We surface an Information
            // line only when a device transitions from offline → online.
            var wasOnline = cs.IsOnline(DateTime.UtcNow);
            cs.MarkConnected();
            // Persist the device immediately so CSV uploads (FK → device) always have a parent row.
            await _deviceStore.UpsertAsync(cs, CancellationToken.None);
            if (!wasOnline)
                _logger.LogInformation("Bluetooth device online: {Name} ({Addr}).", name, address);
            else
                _logger.LogDebug("Bluetooth connection opened: {Name} ({Addr}).", name, address);

            var decoder = new FrameDecoder();
            var buffer = new byte[4096];
            var receivedData = false;
            // Outcome of each CSV file in the current batch (filename → ok), flushed as one summary
            // when the phone sends BATCH_END.
            var batch = new List<(string file, bool ok)>();

            ActiveConn? conn = null;
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    // Wrap the stream so all reply writes go through one serialized helper.
                    conn = new ActiveConn { Stream = stream, Name = name, Address = address };

                    int read;
                    // net48 Stream has no Memory<byte> overload → use the classic (byte[],int,int) one.
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        foreach (var frame in decoder.Push(buffer, read))
                        {
                            // Log EVERY inbound frame (first line + length) so req/res can be traced.
                            _logger.LogInformation(
                                "BT RECV from {Name} ({Addr}): {Len} chars, first line: \"{First}\"",
                                name, address, frame.Length, FirstLine(frame));

                            if (IsHeartbeat(frame))
                            {
                                await HandleHeartbeatAsync(frame, conn, cs, name, address, token);
                            }
                            else if (IsMasterRequest(frame))
                            {
                                // Phone asks for master files → send the ones the PC has newer of.
                                await HandleMasterRequestAsync(frame, conn, name, address, token);
                            }
                            else if (IsBatchEnd(frame))
                            {
                                // Phone finished the batch → reply once with all files' outcomes.
                                await SendBatchResultAsync(conn, batch, name, address, token);
                                batch.Clear();
                            }
                            else
                            {
                                receivedData = true;
                                cs.AddFrame();
                                var result = await IngestFrameAsync(frame, cs, name, address, token);
                                batch.Add(result);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Bluetooth read from {Name} ({Addr}) ended: {Msg}", name, address, ex.Message);
            }
            finally
            {
                cs.MarkDisconnected();
                if (receivedData)
                    cs.AddDataSession();
                _logger.LogDebug("Bluetooth connection closed: {Name} ({Addr}).", name, address);
                // Persist the device's final state so it survives an app restart.
                await _deviceStore.UpsertAsync(cs, CancellationToken.None);
            }
        }

        private static bool IsHeartbeat(string frame) =>
            frame.StartsWith(HeartbeatPing, StringComparison.OrdinalIgnoreCase);

        private static bool IsBatchEnd(string frame) =>
            frame.Trim().Equals(BatchEnd, StringComparison.OrdinalIgnoreCase);

        private static bool IsMasterRequest(string frame) =>
            frame.StartsWith(MasterRequest, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Handles a liveness ping ("PING,&lt;deviceName&gt;,&lt;epochMillis&gt;"): records it on the
        /// client status and replies with a framed "PONG,&lt;radioName&gt;,&lt;epochMillis&gt;" so the
        /// phone can confirm the listener is alive and responding (not just RFCOMM-reachable).
        /// </summary>
        private async Task HandleHeartbeatAsync(
            string frame, ActiveConn conn, BtClientStatus cs, string name, string address, CancellationToken token)
        {
            // frame = PING,<deviceName>,<epochMillis> — pull the device name so the dashboard
            // can label a phone that has only ever pinged (never sent a CSV).
            var parts = frame.Split(',');
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                cs.SetDevice(parts[1].Trim());

            cs.AddHeartbeat();
            _logger.LogDebug("Heartbeat from {Name} ({Addr}); replying PONG.", name, address);
            await _deviceStore.UpsertAsync(cs, token);

            try
            {
                var radio = _status.RadioName ?? _serviceName ?? "";
                var nowMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SendTextFrameAsync(conn, $"{HeartbeatPong},{radio},{nowMillis}", token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to send PONG to {Name} ({Addr}): {Msg}", name, address, ex.Message);
            }
        }

        /// <summary>
        /// Handle a master pull request. The frame is
        /// <c>MASTER_REQ\n&lt;name&gt;=&lt;lastModifiedMillis&gt;\n…</c> carrying the phone's current master
        /// files' last-modified time. For each master the PC modified more recently (PC mtime &gt; the
        /// phone's value, or the phone doesn't have it) the PC streams the file; then a single
        /// <c>MASTER_END\n&lt;name&gt;=UPDATED|UPTODATE\n…</c> frame reports every master's status so the
        /// phone can show an accurate message (updated N / already current).
        /// </summary>
        private async Task HandleMasterRequestAsync(
            string frame, ActiveConn conn, string name, string address, CancellationToken token)
        {
            // Full request/response logging so we can diagnose the "always up-to-date" report
            // (e.g. a phone clock running ahead of the PC). Logged at Information so it shows in the
            // console without turning on Debug.
            _logger.LogInformation(
                "MASTER_REQ from {Name} ({Addr}) — raw frame:\n{Frame}", name, address, frame);
            var appTimes = ParseMasterRequest(frame);
            var statuses = new List<string>();
            int sent = 0;
            try
            {
                foreach (var kind in new[] { MasterKind.Customer, MasterKind.Item })
                {
                    var fileName = _master.FileName(kind);
                    long appMillis = appTimes.TryGetValue(fileName, out var t) ? t : 0;
                    long pcMillis = _master.LastModifiedUnixMillis(kind);
                    bool newer = pcMillis > appMillis;
                    // ISO forms make the clock comparison human-readable in the log.
                    _logger.LogInformation(
                        "MASTER compare {File}: pc-mtime={Pc} ({PcIso}) vs app-mtime={App} ({AppIso}) → {Decision}",
                        fileName, pcMillis, IsoUtc(pcMillis), appMillis, IsoUtc(appMillis),
                        newer ? "SEND (pc newer)" : "SKIP (app >= pc)");

                    if (newer)
                    {
                        var file = _master.Load(kind);
                        // Line 1 carries the PC-origin mtime so the phone stores IT (not its own clock)
                        // and echoes it back next time — keeps the compare in the PC clock domain.
                        await SendTextFrameAsync(conn, fileName + "\t" + pcMillis + "\r\n" + file.Csv, token);
                        sent++;
                        statuses.Add($"{fileName}={MasterUpdated}");
                        _logger.LogInformation(
                            "MASTER → {Name} ({Addr}): SENT {File} ({Bytes} chars).",
                            name, address, fileName, file.Csv.Length);
                    }
                    else
                    {
                        statuses.Add($"{fileName}={MasterUpToDate}");
                    }
                }
                // Closing frame carries EVERY master's status so the phone can report accurately.
                var endPayload = MasterEnd + "\n" + string.Join("\n", statuses);
                await SendTextFrameAsync(conn, endPayload, token);
                _logger.LogInformation(
                    "MASTER_END → {Name} ({Addr}): {Sent} file(s) sent. Response frame:\n{Frame}",
                    name, address, sent, endPayload);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to serve master request from {Name} ({Addr}): {Msg}", name, address, ex.Message);
            }
        }

        /// <summary>Epoch ms → readable UTC ISO for logs; "(none)" when 0/absent.</summary>
        private static string IsoUtc(long millis) =>
            millis <= 0 ? "(none)" : DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'");

        /// <summary>Parse "MASTER_REQ\n&lt;name&gt;=&lt;millis&gt;\n…" into a name→last-modified map.</summary>
        private static Dictionary<string, long> ParseMasterRequest(string frame)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var lines = frame.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var line in lines)
            {
                var l = line.Trim();
                if (l.Length == 0 || l.Equals(MasterRequest, StringComparison.OrdinalIgnoreCase)) continue;
                int eq = l.LastIndexOf('=');
                if (eq <= 0) continue;
                var key = l[..eq].Trim();
                if (long.TryParse(l[(eq + 1)..].Trim(), out var millis))
                    map[key] = millis;
            }
            return map;
        }

        /// <summary>
        /// Write one STX/ETX-framed text payload to a connection, serialized by its write lock so it
        /// never interleaves with another writer (PONG/RESULT from the read loop, or a concurrent push).
        /// </summary>
        private async Task SendTextFrameAsync(ActiveConn conn, string payload, CancellationToken token)
        {
            // Log EVERY outbound frame (first line + length) so req/res can be traced.
            _logger.LogInformation(
                "BT SEND to {Name} ({Addr}): {Len} chars, first line: \"{First}\"",
                conn.Name, conn.Address, payload.Length, FirstLine(payload));

            var body = Encoding.UTF8.GetBytes(payload);
            var packet = new byte[body.Length + 2];
            packet[0] = FrameDecoder.STX;
            Array.Copy(body, 0, packet, 1, body.Length);
            packet[^1] = FrameDecoder.ETX;

            await conn.WriteLock.WaitAsync(token);
            try
            {
                await conn.Stream.WriteAsync(packet, 0, packet.Length, token);
                await conn.Stream.FlushAsync(token);
            }
            finally
            {
                conn.WriteLock.Release();
            }
        }

        /// <summary>First line of a frame (for concise req/res logging).</summary>
        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? s : s[..nl];
        }

        /// <summary>
        /// Ingests one CSV frame and returns its outcome (parsed filename + ok) so the caller can
        /// include it in the batch summary. Never throws.
        /// </summary>
        private async Task<(string file, bool ok)> IngestFrameAsync(
            string frame, BtClientStatus cs, string name, string address, CancellationToken token)
        {
            // Parsed filename echoed back in the batch result so the phone can correlate the outcome
            // with the exact file it sent. Declared outside the try so the catch can report it too.
            string filename = "";
            try
            {
                // The frame's FIRST line may be the upload filename
                // ({type}_{yyyyMMdd}_{index}_{termId}.txt); the rest is the CSV (its own first
                // line is the header). Split them.
                string csv = frame;
                int nl = frame.IndexOf('\n');
                if (nl >= 0)
                {
                    var line1 = frame[..nl].TrimEnd('\r').Trim();
                    if (CsvTypes.IsFilenameLine(line1))
                    {
                        filename = line1;
                        csv = frame[(nl + 1)..];
                    }
                }

                var headerEnd = csv.IndexOf('\n');
                var header = (headerEnd >= 0 ? csv[..headerEnd] : csv).TrimEnd('\r').Trim();
                var type = CsvTypes.DetectType(header);

                var (fType, index, termId, logDate) = filename.Length > 0
                    ? CsvTypes.ParseFilename(filename)
                    : (CsvType.Unknown, 0, "", (DateOnly?)null);
                if (type == CsvType.Unknown && fType != CsvType.Unknown) type = fType; // trust filename
                if (string.IsNullOrWhiteSpace(termId)) termId = cs.WorkerId ?? name;

                // Typed (monitor/pallet/direct) or unknown → stored & normalized by CsvStore.
                int received = type switch
                {
                    CsvType.MonitorLog => CsvTypes.ParseMonitor(csv).Count,
                    CsvType.PalletLog => CsvTypes.ParsePallet(csv).Count,
                    CsvType.DirectLog => CsvTypes.ParseDirect(csv).Count,
                    _ => CountNonHeaderRows(csv),
                };
                cs.SetDevice(termId);
                cs.AddRecords(received);

                if (received == 0 && type != CsvType.PalletLog && type != CsvType.MonitorLog)
                {
                    _logger.LogWarning("Empty/unparseable frame from {Name} ({Addr}) discarded.", name, address);
                    return (filename, false);
                }

                // Persist the device first (FK parent), then the CSV upload (raw text kept so its
                // rows can be re-displayed; CsvStore also normalizes into per-type tables).
                await _deviceStore.UpsertAsync(cs, token);
                await _csvStore.SaveAsync(new Models.CsvUpload
                {
                    // DeviceAddress is transient — CsvStore resolves it to the device's DeviceId.
                    DeviceAddress = address,
                    Source = "Bluetooth",
                    Type = CsvTypes.TypeKey(type),
                    TermId = termId,
                    UploadIndex = index,
                    // Log day from the filename; fall back to the local date we received it on.
                    LogDate = logDate?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now.Date,
                    RowCount = received,
                    RawCsv = csv,
                }, token);

                // Keep a raw on-disk copy of the received file (best-effort; never fails ingestion).
                await _backup.SaveAsync(filename, csv, type, termId, index, logDate, token);

                _logger.LogInformation(
                    "Ingested {Type} CSV from {Name} ({Addr}) term='{Term}' idx={Idx}: rows={Recv}.",
                    CsvTypes.TypeKey(type), name, address, termId, index, received);

                return (filename, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (filename, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest frame from {Name} ({Addr}).", name, address);
                return (filename, false);
            }
        }

        /// <summary>
        /// Sends the single batch summary "RESULT\n&lt;filename&gt;=OK\n&lt;filename&gt;=ERR\n…" (STX/ETX
        /// framed) once the phone signals BATCH_END, so it can move/delete only the files that
        /// actually landed. Best-effort.
        /// </summary>
        private async Task SendBatchResultAsync(
            ActiveConn conn, List<(string file, bool ok)> batch, string name, string address, CancellationToken token)
        {
            try
            {
                var sb = new StringBuilder(BatchResultHeader).Append('\n');
                foreach (var (file, ok) in batch)
                    sb.Append(file).Append('=').Append(ok ? "OK" : "ERR").Append('\n');

                await SendTextFrameAsync(conn, sb.ToString(), token);
                _logger.LogInformation(
                    "Sent batch RESULT to {Name} ({Addr}): {Count} file(s), OK={Ok}.",
                    name, address, batch.Count, batch.Count(b => b.ok));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to send batch RESULT to {Name} ({Addr}): {Msg}", name, address, ex.Message);
            }
        }

        /// <summary>Count non-empty data rows in a CSV (excludes the header line).</summary>
        private static int CountNonHeaderRows(string csv)
        {
            var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int n = 0;
            bool first = true;
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (first) { first = false; continue; } // skip header
                n++;
            }
            return n;
        }

        private static string? SafeAddress(BluetoothClient client)
        {
            try
            {
                // 32feet 4.x exposes the underlying socket; its remote endpoint is a
                // BluetoothEndPoint carrying the device address.
                if (client.Client?.RemoteEndPoint is BluetoothEndPoint bep)
                    return bep.Address.ToString();
            }
            catch { /* not available on this backend */ }
            return null;
        }

        private static string SafeName(BluetoothClient client)
        {
            try
            {
                var n = client.RemoteMachineName;
                return string.IsNullOrWhiteSpace(n) ? "Thiết bị BT" : n;
            }
            catch { return "Thiết bị BT"; }
        }
    }
}
