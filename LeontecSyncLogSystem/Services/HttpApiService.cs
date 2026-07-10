using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LeontecSyncLogSystem.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// In-process HTTP monitoring API using <see cref="HttpListener"/> (built into .NET Framework),
    /// replacing the ASP.NET Core Kestrel host that isn't available on net48. Serves the same routes:
    /// <list type="bullet">
    ///   <item><c>GET /api/status</c> — the full monitoring snapshot (same data the dashboard shows).</item>
    ///   <item><c>GET /health</c> — <c>{"status":"ok"}</c>.</item>
    ///   <item><c>POST /api/sync</c> — 501 (Wi-Fi ingest is parked, same as before).</item>
    /// </list>
    /// Best-effort: if the port can't be bound (e.g. no admin urlacl for all-interfaces), it falls back
    /// to localhost and, failing that, disables the API with a warning — the dashboard reads state
    /// in-process and never depends on this HTTP server.
    /// </summary>
    public sealed class HttpApiService : IHostedService
    {
        private readonly MonitorService _monitor;
        private readonly ILogger<HttpApiService> _logger;
        private readonly int _port;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public HttpApiService(MonitorService monitor, IConfiguration config, ILogger<HttpApiService> logger)
        {
            _monitor = monitor;
            _logger = logger;
            // Reuse the legacy "Kestrel:Endpoints:Http:Url" setting so appsettings.json stays unchanged.
            _port = ExtractPort(config["Kestrel:Endpoints:Http:Url"], 8090);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = new CancellationTokenSource();
            // "+" binds all interfaces but may need an admin urlacl; fall back to localhost-only.
            if (!TryStart($"http://+:{_port}/") && !TryStart($"http://localhost:{_port}/"))
            {
                _logger.LogWarning(
                    "HTTP monitoring API disabled (could not bind port {Port}). The dashboard still works in-process.",
                    _port);
                return Task.CompletedTask;
            }

            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            }
            try { _listener?.Close(); } catch { /* ignore */ }
        }

        private bool TryStart(string prefix)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                _logger.LogInformation(
                    "HTTP monitoring API listening on {Prefix} (GET /api/status, GET /health, POST /api/sync→501).",
                    prefix);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("HTTP bind failed for {Prefix}: {Msg}", prefix, ex.Message);
                return false;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested || _listener is null || !_listener.IsListening)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("HTTP accept error: {Msg}", ex.Message);
                    continue;
                }

                // Handle each request without blocking the accept loop.
                _ = HandleRequestAsync(context, token);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                var method = context.Request.HttpMethod;

                if (method == "GET" && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = await _monitor.GetSnapshotAsync(token).ConfigureAwait(false);
                    await WriteJsonAsync(context, 200, JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
                }
                else if (method == "GET" && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context, 200, "{\"status\":\"ok\"}").ConfigureAwait(false);
                }
                else if (method == "POST" && path.Equals("/api/sync", StringComparison.OrdinalIgnoreCase))
                {
                    // Wi-Fi ingest is parked (legacy CSV path removed) — same 501 as the Kestrel version.
                    await WriteJsonAsync(context, 501, "{\"error\":\"Wi-Fi ingest is not enabled.\"}").ConfigureAwait(false);
                }
                else
                {
                    await WriteJsonAsync(context, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("HTTP request handling error: {Msg}", ex.Message);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* ignore */ }
            }
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        /// <summary>Extract the port from a "http://host:port" URL; returns <paramref name="fallback"/> on failure.</summary>
        private static int ExtractPort(string? url, int fallback)
        {
            if (string.IsNullOrWhiteSpace(url)) return fallback;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
            return fallback;
        }
    }
}
