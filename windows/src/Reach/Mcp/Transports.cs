using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Reach.Mcp;

/// <summary>
/// MCP over stdio: one JSON-RPC message per line in, one per line out. Messages are handled side by side, so a
/// long wait_for never holds up a ping. Logs go to stderr only.
/// </summary>
public static class StdioServer
{
    public static async Task RunAsync(McpHandler handler)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var gate = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = line;
            running.Add(Task.Run(async () =>
            {
                var reply = await handler.HandleAsync(msg);
                if (reply == null) return;
                await gate.WaitAsync();
                try { await stdout.WriteLineAsync(reply); }
                finally { gate.Release(); }
            }));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }
}

/// <summary>
/// MCP Streamable HTTP, the part Reach needs (spec/CONTRACT.md §1), the same as Reach for Android's: POST /mcp,
/// JSON reply, bearer token, Origin check, GET /health. Plain sockets, so no URL reservations or admin rights.
/// </summary>
public sealed class HttpServer(McpHandler handler, Func<string> token, int port, bool allNetworks, Func<IPAddress, bool>? allow = null)
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    public int BoundPort { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(allNetworks ? IPAddress.Any : IPAddress.Loopback, port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            // Tailscale mode listens everywhere but only lets this PC and Tailscale devices in.
            if (allow != null && c.Client.RemoteEndPoint is IPEndPoint ep && !allow(ep.Address)) { c.Dispose(); continue; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string n) => Headers.GetValueOrDefault(n.ToLowerInvariant());
    }

    private sealed record Response(int Status, string Type, string Body, Dictionary<string, string>? Extra = null);

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            c.ReceiveTimeout = 60_000;
            var s = c.GetStream();
            while (true)
            {
                Request? req;
                try { req = await ReadRequest(s); } catch { return; }
                if (req == null) return;
                var keepAlive = !string.Equals(req.Header("connection"), "close", StringComparison.OrdinalIgnoreCase);
                var resp = await Route(req);
                try { await Write(s, resp, keepAlive); } catch { return; }
                if (!keepAlive) return;
            }
        }
    }

    private async Task<Response> Route(Request req)
    {
        var path = req.Path.Split('?')[0];
        if (path == "/health" && req.Method == "GET") return new(200, "text/plain", "reach ok");
        if (path != "/mcp") return new(404, "text/plain", "not found");

        var origin = req.Header("origin");
        if (origin != null)
        {
            var host = origin.Contains("://") ? origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..] : origin;
            host = host.Split('/')[0];
            if (host.StartsWith('[')) host = host[..(host.IndexOf(']') + 1)];
            else if (host.Contains(':')) host = host[..host.LastIndexOf(':')];
            if (host is not ("localhost" or "127.0.0.1" or "[::1]")) return new(403, "text/plain", "origin not allowed");
        }
        if (!Authorized(req.Header("authorization")))
            return new(401, "application/json", "{\"error\":\"missing or wrong Reach token\"}", new() { ["WWW-Authenticate"] = "Bearer" });

        switch (req.Method)
        {
            case "POST":
                var reply = await handler.HandleAsync(Encoding.UTF8.GetString(req.Body));
                var session = req.Header("mcp-session-id") ?? Guid.NewGuid().ToString();
                return reply == null
                    ? new(202, "text/plain", "", new() { ["Mcp-Session-Id"] = session })
                    : new(200, "application/json", reply, new() { ["Mcp-Session-Id"] = session });
            case "DELETE":
                return new(200, "text/plain", "");
            default:
                return new(405, "text/plain", "use POST", new() { ["Allow"] = "POST" });
        }
    }

    private bool Authorized(string? header)
    {
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var want = token();
        if (want.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..].Trim()), Encoding.UTF8.GetBytes(want));
    }

    private static async Task<Request?> ReadRequest(Stream s)
    {
        var first = await ReadLine(s);
        if (first == null) return null;
        var parts = first.Split(' ');
        if (parts.Length < 2) return null;
        var headers = new Dictionary<string, string>();
        while (true)
        {
            var line = await ReadLine(s);
            if (line == null) return null;
            if (line.Length == 0) break;
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
            if (headers.Count > 100) return null;
        }
        var len = int.TryParse(headers.GetValueOrDefault("content-length"), out var l) ? l : 0;
        if (len is < 0 or > 2 * 1024 * 1024) return null;
        var body = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await s.ReadAsync(body.AsMemory(read, len - read));
            if (n <= 0) return null;
            read += n;
        }
        return new Request(parts[0].ToUpperInvariant(), parts[1], headers, body);
    }

    private static async Task<string?> ReadLine(Stream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n <= 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == '\n') break;
            if (one[0] != '\r') buf.Add(one[0]);
            if (buf.Count > 8192) return null;
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static async Task Write(Stream s, Response r, bool keepAlive)
    {
        var body = Encoding.UTF8.GetBytes(r.Body);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        head.Append("Content-Type: ").Append(r.Type).Append("; charset=utf-8\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        if (r.Extra != null) foreach (var (k, v) in r.Extra) head.Append(k).Append(": ").Append(v).Append("\r\n");
        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK", 202 => "Accepted", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error",
    };
}
