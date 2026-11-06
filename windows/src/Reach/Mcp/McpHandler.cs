using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reach.Mcp;

/// <summary>
/// MCP over JSON-RPC 2.0, the same behaviour as Reach for Android's McpHandler: initialize, ping, tools/list,
/// tools/call. One message in, one reply out (null for notifications). Every call has a hard time limit.
/// </summary>
public sealed class McpHandler(
    IReadOnlyList<ToolDef> tools,
    Func<string, JsonObject, CancellationToken, Task<ToolResult>> run,
    string serverName,
    string serverVersion,
    string instructions,
    Func<ToolDef, bool>? listed = null)
{
    public static readonly HashSet<string> Supported = new() { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    public const string DefaultVersion = "2025-06-18";

    /// <summary>What each tool's replies cost the agent so far (device shows it).</summary>
    public Core.Tokens Tokens { get; } = new();

    public async Task<string?> HandleAsync(string body)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (msg is JsonArray) return Error(null, -32600, "Batches are not supported");
        if (msg is not JsonObject o) return Error(null, -32600, "Invalid request");
        var id = o["id"]?.DeepClone();
        var method = o["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        if (method == null || id == null) return null; // replies to us, notifications
        var p = o["params"] as JsonObject ?? new JsonObject();

        return method switch
        {
            "initialize" => Result(id, Initialize(p)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(tools.Where(t => listed?.Invoke(t) ?? true).Select(Describe).ToArray()) }),
            "tools/call" => Result(id, await CallAndCountAsync(p)),
            "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
            "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => Error(id, -32601, $"Method not found: {method}"),
        };
    }

    private JsonObject Initialize(JsonObject p)
    {
        var asked = p.Str("protocolVersion");
        return new JsonObject
        {
            ["protocolVersion"] = asked != null && Supported.Contains(asked) ? asked : DefaultVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = serverVersion },
            ["instructions"] = instructions,
        };
    }

    private static JsonNode Describe(ToolDef t) => new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = t.Schema.DeepClone(),
    };

    private async Task<JsonObject> CallAndCountAsync(JsonObject p)
    {
        var r = await CallAsync(p);
        Tokens.Add(p.Str("name") ?? "?", r);
        return r.ToJson();
    }

    private async Task<ToolResult> CallAsync(JsonObject p)
    {
        var name = p.Str("name");
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool == null) return ToolResult.Error("not_found", $"no tool named '{name}'; tools: {string.Join(", ", tools.Select(t => t.Name))}");
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        using var cts = new CancellationTokenSource(tool.TimeoutMs);
        // Run detached: even work that ignores the token can't hold the reply past its time limit.
        var work = Task.Run(() => run(tool.Name, (JsonObject)args.DeepClone(), cts.Token));
        var done = await Task.WhenAny(work, Task.Delay(tool.TimeoutMs + 500));
        if (done != work)
        {
            cts.Cancel();
            return ToolResult.Error("timeout", $"{tool.Name} took longer than {tool.TimeoutMs / 1000} s and was stopped; observe, then try again");
        }
        try { return await work; }
        catch (ToolError e) { return ToolResult.Error(e.Code, e.Hint); }
        catch (OperationCanceledException) { return ToolResult.Error("timeout", $"{tool.Name} took longer than {tool.TimeoutMs / 1000} s and was stopped"); }
        catch (Exception e) { return ToolResult.Error("internal", Trim(e.Message)); }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private static string Result(JsonNode id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
