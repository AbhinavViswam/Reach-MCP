using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Reach.Mcp;
using Reach.Server;

namespace Reach.Tests;

public class ServerTests
{
    private static readonly ToolDef[] Defs =
    {
        new("echo", "echo", new JsonObject { ["type"] = "object" }),
        new("slow", "never answers in time", new JsonObject { ["type"] = "object" }, TimeoutMs: 300),
    };

    private static McpHandler Handler() => new(Defs, async (name, a, ct) =>
    {
        if (name == "slow") { Thread.Sleep(3_000); return ToolResult.Text("late"); } // ignores the token, like a stuck call
        if (a.Str("text") is not string t) throw new ToolError("bad_args", "text needed");
        await Task.Yield();
        return ToolResult.Text(t);
    }, "reach-test", "0", "hi");

    private static JsonObject J(string? s) => JsonNode.Parse(s!)!.AsObject();

    [Fact]
    public async Task InitializeListCall()
    {
        var h = Handler();
        var init = J(await h.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""));
        Assert.Equal("2025-03-26", (string?)init["result"]!["protocolVersion"]);
        var odd = J(await h.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999"}}"""));
        Assert.Equal(McpHandler.DefaultVersion, (string?)odd["result"]!["protocolVersion"]);
        Assert.Null(await h.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var list = J(await h.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}"""));
        Assert.Equal(2, list["result"]!["tools"]!.AsArray().Count);
        var call = J(await h.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{"text":"नमस्ते"}}}"""));
        Assert.Equal("नमस्ते", (string?)call["result"]!["content"]![0]!["text"]);
        var bad = J(await h.HandleAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":{}}}"""));
        Assert.True((bool)bad["result"]!["isError"]!);
        Assert.Equal(-32700, (int)J(await h.HandleAsync("{nope"))["error"]!["code"]!);
    }

    [Fact]
    public async Task StuckCallTimesOutOnTime()
    {
        var sw = Stopwatch.StartNew();
        var r = await Handler().HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slow"}}""");
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains("error: timeout", r);
    }

    private static async Task<int> Post(int port, string body, params string[] headers)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var s = c.GetStream();
        var b = Encoding.UTF8.GetBytes(body);
        var head = string.Join("\r\n", new[] { "POST /mcp HTTP/1.1", "Host: x", "Content-Type: application/json" }
            .Concat(headers).Concat(new[] { $"Content-Length: {b.Length}", "Connection: close", "", "" }));
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(b);
        var first = await new StreamReader(s).ReadLineAsync();
        return int.Parse(first!.Split(' ')[1]);
    }

    [Fact]
    public async Task HttpNeedsTokenAndRefusesWebPages()
    {
        var server = new HttpServer(Handler(), () => "secret", 0, allNetworks: false);
        server.Start();
        try
        {
            var ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
            Assert.Equal(401, await Post(server.BoundPort, ping));
            Assert.Equal(401, await Post(server.BoundPort, ping, "Authorization: Bearer nope"));
            Assert.Equal(403, await Post(server.BoundPort, ping, "Authorization: Bearer secret", "Origin: https://evil.example"));
            Assert.Equal(200, await Post(server.BoundPort, ping, "Authorization: Bearer secret", "Origin: http://localhost:5173"));
            Assert.Equal(200, await Post(server.BoundPort, ping, "Authorization: Bearer secret"));
            Assert.Equal(202, await Post(server.BoundPort, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", "Authorization: Bearer secret"));
            using var http = new HttpClient();
            Assert.Equal("reach ok", await http.GetStringAsync($"http://127.0.0.1:{server.BoundPort}/health"));
            // Keep-alive: several calls on one connection.
            for (var i = 0; i < 3; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.BoundPort}/mcp") { Content = new StringContent(ping, Encoding.UTF8, "application/json") };
                req.Headers.Add("Authorization", "Bearer secret");
                Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);
            }
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task WatchdogRestartsAHungWorker()
    {
        // The exe as built by the src project (the copy in the test folder lacks its runtime settings).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.Name != "windows") dir = dir.Parent;
        var exe = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Reach", "bin", "Debug"), "reach.exe", SearchOption.AllDirectories).First();
        Environment.SetEnvironmentVariable("REACH_TEST_HOOKS", "1");
        using var host = new WorkerHost(Array.Empty<string>(), exe);
        var sw = Stopwatch.StartNew();
        var r = await host.CallAsync("__hang", new JsonObject(), 3_000, CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("restarted", r.Content[0].Text);
        Assert.True(sw.ElapsedMilliseconds < 4_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, host.Restarts);
        // The next call gets a fresh worker that answers.
        var dev = await host.CallAsync("device", new JsonObject(), 20_000, CancellationToken.None);
        Assert.False(dev.IsError, dev.Content[0].Text);
        Assert.Contains("reach ", dev.Content[0].Text);
    }
}
