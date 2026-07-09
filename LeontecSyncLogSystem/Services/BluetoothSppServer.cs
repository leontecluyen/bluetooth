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

        private readonly ServiceStatus _status;
        private readonly ICsvStore _csvStore;
        private readonly IDeviceStore _deviceStore;
        private readonly ICsvBackupWriter _backup;
        private readonly string _serviceName;
        private readonly ILogger _logger;

        public BluetoothSppServer(
            ServiceStatus status, ICsvStore csvStore, IDeviceStore deviceStore,
            ICsvBackupWriter backup, string serviceName, ILogger logger)
        {
            _status = status;
            _csvStore = csvStore;
            _deviceStore = deviceStore;
            _backup = backup;
            _serviceName = serviceName;
            _logger = logger;
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

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                    {
                        foreach (var frame in decoder.Push(buffer, read))
                        {
                            if (IsHeartbeat(frame))
                            {
                                await HandleHeartbeatAsync(frame, stream, cs, name, address, token);
                            }
                            else if (IsBatchEnd(frame))
                            {
                                // Phone finished the batch → reply once with all files' outcomes.
                                await SendBatchResultAsync(stream, batch, name, address, token);
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

        /// <summary>
        /// Handles a liveness ping ("PING,&lt;deviceName&gt;,&lt;epochMillis&gt;"): records it on the
        /// client status and replies with a framed "PONG,&lt;radioName&gt;,&lt;epochMillis&gt;" so the
        /// phone can confirm the listener is alive and responding (not just RFCOMM-reachable).
        /// </summary>
        private async Task HandleHeartbeatAsync(
            string frame, Stream stream, BtClientStatus cs, string name, string address, CancellationToken token)
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
                var reply = $"{HeartbeatPong},{radio},{nowMillis}";
                var body = Encoding.UTF8.GetBytes(reply);

                var packet = new byte[body.Length + 2];
                packet[0] = FrameDecoder.STX;
                Array.Copy(body, 0, packet, 1, body.Length);
                packet[^1] = FrameDecoder.ETX;

                await stream.WriteAsync(packet, token);
                await stream.FlushAsync(token);
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
            Stream stream, List<(string file, bool ok)> batch, string name, string address, CancellationToken token)
        {
            try
            {
                var sb = new StringBuilder(BatchResultHeader).Append('\n');
                foreach (var (file, ok) in batch)
                    sb.Append(file).Append('=').Append(ok ? "OK" : "ERR").Append('\n');

                var body = Encoding.UTF8.GetBytes(sb.ToString());
                var packet = new byte[body.Length + 2];
                packet[0] = FrameDecoder.STX;
                Array.Copy(body, 0, packet, 1, body.Length);
                packet[^1] = FrameDecoder.ETX;
                await stream.WriteAsync(packet, token);
                await stream.FlushAsync(token);
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
