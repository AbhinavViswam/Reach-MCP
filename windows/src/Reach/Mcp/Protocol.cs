using System.Text.Json.Nodes;

namespace Reach.Mcp;

/// <summary>Width/Height (pixels) travel between worker and server for the token estimate; MCP clients never see them.</summary>
public sealed record Content(string Type, string? Text = null, string? Data = null, string? Mime = null, int Width = 0, int Height = 0)
{
    public static Content Of(string text) => new("text", Text: text);
    public static Content Image(string base64, string mime, int width = 0, int height = 0) => new("image", Data: base64, Mime: mime, Width: width, Height: height);
}

public sealed record ToolResult(IReadOnlyList<Content> Content, bool IsError = false)
{
    public static ToolResult Text(string s) => new(new[] { Mcp.Content.Of(s) });

    /// <summary>Contract §4: an error code, then one line saying what to do next.</summary>
    public static ToolResult Error(string code, string hint) => new(new[] { Mcp.Content.Of($"error: {code} · {hint}") }, true);

    /// <param name="sizes">Add image width/height (worker to server only; not part of MCP).</param>
    public JsonObject ToJson(bool sizes = false)
    {
        var arr = new JsonArray();
        foreach (var c in Content)
        {
            if (c.Type != "image") { arr.Add(new JsonObject { ["type"] = "text", ["text"] = c.Text }); continue; }
            var o = new JsonObject { ["type"] = "image", ["data"] = c.Data, ["mimeType"] = c.Mime };
            if (sizes) { o["width"] = c.Width; o["height"] = c.Height; }
            arr.Add(o);
        }
        return new JsonObject { ["content"] = arr, ["isError"] = IsError };
    }

    public static ToolResult FromJson(JsonObject o)
    {
        var list = new List<Content>();
        foreach (var n in o["content"]?.AsArray() ?? new JsonArray())
        {
            var c = n!.AsObject();
            list.Add((string?)c["type"] == "image"
                ? Mcp.Content.Image((string)c["data"]!, (string)c["mimeType"]!, (int?)c["width"] ?? 0, (int?)c["height"] ?? 0)
                : Mcp.Content.Of((string?)c["text"] ?? ""));
        }
        return new ToolResult(list, (bool?)o["isError"] ?? false);
    }
}

/// <summary>A failure that reaches the agent as a tool error with a hint, not as a crash.</summary>
public sealed class ToolError(string code, string hint) : Exception($"{code}: {hint}")
{
    public string Code { get; } = code;
    public string Hint { get; } = hint;
}

public static class Args
{
    public static string? Str(this JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? Int(this JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    public static bool? Bool(this JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        return null;
    }
}
