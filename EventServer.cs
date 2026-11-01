using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentDock;

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 only. A raw TcpListener avoids the URL ACL
/// registration that HttpListener needs for non-admin users.
///   POST /event     hook event JSON          (token required)
///   GET  /health    "ok"
///   GET  /sessions  current session state    (token required, for debugging)
///
/// Security: every request must carry a Host header of 127.0.0.1/localhost (blocks DNS
/// rebinding), and data endpoints need the per-install secret in X-SessionDock-Token
/// (X-Sessiondex-Token is still accepted from hook scripts installed by 1.0).
/// Browsers can't send that custom header cross-origin without a CORS preflight, which
/// this server never approves, so web pages can't post or read events.
/// </summary>
public sealed class EventServer : IDisposable
{
    const int MaxBody = 256 * 1024;
    const int MaxConcurrent = 16;

    readonly TcpListener _listener;
    readonly int _port;
    readonly byte[] _token;
    readonly Action<JsonObject> _onEvent;
    readonly Func<string> _snapshot;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _slots = new(MaxConcurrent);

    public EventServer(int port, string token, Action<JsonObject> onEvent, Func<string> snapshot)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _port = port;
        _token = Encoding.UTF8.GetBytes(token);
        _onEvent = onEvent;
        _snapshot = snapshot;
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }
            if (!_slots.Wait(0))
            {
                client.Dispose(); // too many open connections; drop
                continue;
            }
            _ = Handle(client).ContinueWith(_ => _slots.Release(), TaskScheduler.Default);
        }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var stream = client.GetStream();

                var received = new MemoryStream();
                var buffer = new byte[8192];
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int n = await stream.ReadAsync(buffer, cts.Token);
                    if (n == 0) return;
                    received.Write(buffer, 0, n);
                    headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
                    if (headerEnd < 0 && received.Length > 64 * 1024) return;
                }

                byte[] data = received.ToArray();
                string[] lines = Encoding.ASCII.GetString(data, 0, headerEnd).Split("\r\n");
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2) return;
                string method = requestLine[0];
                string path = requestLine[1];

                int length = 0;
                string host = "", token = "", contentType = "";
                bool hasOrigin = false;
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
                    else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                    else if (name.Equals("X-SessionDock-Token", StringComparison.OrdinalIgnoreCase)
                          || name.Equals("X-Sessiondex-Token", StringComparison.OrdinalIgnoreCase)) token = value;
                    else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                    else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) hasOrigin = true;
                }

                // DNS rebinding guard: only accept requests addressed to loopback by name.
                if (host != $"127.0.0.1:{_port}" && host != $"localhost:{_port}")
                {
                    await Respond(stream, "421 Misdirected Request", "", cts.Token);
                    return;
                }
                // Browsers always send Origin on cross-site requests; the hook never does.
                if (hasOrigin)
                {
                    await Respond(stream, "403 Forbidden", "", cts.Token);
                    return;
                }
                if (length < 0 || length > MaxBody)
                {
                    await Respond(stream, "413 Payload Too Large", "", cts.Token);
                    return;
                }
                bool authorized = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token);
                if (path != "/health" && !authorized)
                {
                    await Respond(stream, "401 Unauthorized", "", cts.Token);
                    return;
                }

                var body = new byte[length];
                int bodyStart = headerEnd + 4;
                int have = Math.Min(length, data.Length - bodyStart);
                Array.Copy(data, bodyStart, body, 0, have);
                while (have < length)
                {
                    int n = await stream.ReadAsync(body.AsMemory(have, length - have), cts.Token);
                    if (n == 0) break;
                    have += n;
                }

                if (method == "POST" && path == "/event")
                {
                    if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        await Respond(stream, "415 Unsupported Media Type", "", cts.Token);
                        return;
                    }
                    // Answer first so the hook can exit straight away.
                    await Respond(stream, "204 No Content", "", cts.Token);
                    if (JsonNode.Parse(Encoding.UTF8.GetString(body, 0, have)) is JsonObject ev)
                        _onEvent(ev);
                }
                else if (method == "GET" && path == "/health")
                {
                    await Respond(stream, "200 OK", "ok", cts.Token);
                }
                else if (method == "GET" && path == "/sessions")
                {
                    await Respond(stream, "200 OK", _snapshot(), cts.Token, "application/json; charset=utf-8");
                }
                else
                {
                    await Respond(stream, "404 Not Found", "", cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"Request failed: {ex.Message}");
            }
        }
    }

    static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
            if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                return i - 3;
        return -1;
    }

    static async Task Respond(NetworkStream stream, string status, string body, CancellationToken ct,
        string contentType = "text/plain; charset=utf-8")
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
