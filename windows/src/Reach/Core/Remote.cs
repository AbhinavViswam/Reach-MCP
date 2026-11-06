using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Reach.Mcp;

namespace Reach.Core;

/// <summary>
/// Talks MCP to another Reach (Reach for Android on a phone): initialize once, then tools/list and tools/call over
/// POST /mcp with its bearer token. Used by the phone tool (Server/PhoneHub.cs).
/// </summary>
public sealed class McpClient(string url, string token)
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? _session;
    private bool _ready;
    private long _id;
    public string Url { get; } = url.TrimEnd('/');

    private async Task<JsonObject> Rpc(string method, JsonObject? p, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = method };
        if (p != null) body["params"] = p;
        var req = new HttpRequestMessage(HttpMethod.Post, Url + "/mcp") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (_session != null) req.Headers.Add("Mcp-Session-Id", _session);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolError("timeout", $"the device at {Url} didn't answer in {timeoutMs / 1000} s");
        }
        catch (HttpRequestException e)
        {
            throw new ToolError("unreachable", $"can't reach {Url} ({e.InnerException?.Message ?? e.Message}); is Reach running there, " +
                                               "and for USB is adb forward/reverse set up?");
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new ToolError("bad_token", $"{Url} refused the token; copy it again from that device's Reach app");
            if (!resp.IsSuccessStatusCode) throw new ToolError("unreachable", $"{Url} answered {(int)resp.StatusCode}");
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var s)) _session = s.FirstOrDefault() ?? _session;
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            var o = JsonNode.Parse(text) as JsonObject ?? throw new ToolError("bad_reply", "the device sent something that isn't JSON");
            if (o["error"] is JsonObject err) throw new ToolError("remote_error", (string?)err["message"] ?? "error");
            return o["result"] as JsonObject ?? new JsonObject();
        }
    }

    private async Task Ready(CancellationToken ct)
    {
        if (_ready) return;
        var r = await Rpc("initialize", new JsonObject
        {
            ["protocolVersion"] = McpHandler.DefaultVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "reach-windows", ["version"] = Program.Version },
        }, 10_000, ct);
        ServerName = (string?)r["serverInfo"]?["name"];
        _ready = true;
    }

    public string? ServerName { get; private set; }

    public async Task<List<(string Name, string Description)>> ToolsAsync(CancellationToken ct)
    {
        await Ready(ct);
        var r = await Rpc("tools/list", null, 10_000, ct);
        return (r["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(t => ((string?)t["name"] ?? "", (string?)t["description"] ?? "")).ToList();
    }

    public async Task<ToolResult> CallAsync(string tool, JsonObject args, int timeoutMs, CancellationToken ct)
    {
        await Ready(ct);
        try
        {
            return ToolResult.FromJson(await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, timeoutMs, ct));
        }
        catch (ToolError e) when (e.Code is "remote_error" or "bad_reply")
        {
            _ready = false; // a restarted device: start over next time
            throw;
        }
    }
}

/// <summary>Who may reach reach.exe --http (spec/CONTRACT.md §1).</summary>
public static class Net
{
    /// <summary>This PC, or a device on your Tailscale network (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    public static bool LocalOrTailscale(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork) return b[0] == 100 && (b[1] & 0xC0) == 64;
        return a.AddressFamily == AddressFamily.InterNetworkV6 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    /// <summary>This PC's Tailscale address, if Tailscale is on.</summary>
    public static string? TailscaleAddress()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && LocalOrTailscale(a))?.ToString();
        }
        catch { return null; }
    }
}
