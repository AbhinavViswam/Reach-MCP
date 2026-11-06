using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Server;

/// <summary>
/// The phone tool (spec/CONTRACT.md §6.3): one agent connected to this PC also uses the linked phones. A call names
/// the phone tool to run ("observe", "act", "sms", …) and its arguments; it goes to that phone's Reach over HTTP. The
/// phone's own rules still apply there (its switches, app rules, approvals on the phone, masking).
/// </summary>
public sealed class PhoneHub
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    private McpClient Client(Config.PhoneLink p)
    {
        lock (_clients)
        {
            var key = p.Name + "|" + p.Url + "|" + p.Token;
            if (!_clients.TryGetValue(key, out var c)) _clients[key] = c = new McpClient(p.Url, p.Token);
            return c;
        }
    }

    public static Config.PhoneLink Pick(Config c, string? device)
    {
        if (c.Phones.Count == 0) throw new ToolError("no_phone", "no phone is linked; the user adds one in Reach settings › Phones");
        if (string.IsNullOrWhiteSpace(device))
        {
            if (c.Phones.Count == 1) return c.Phones[0];
            throw new ToolError("bad_args", $"which phone? device = {string.Join(" / ", c.Phones.Select(p => p.Name))}");
        }
        return c.Phones.FirstOrDefault(p => string.Equals(p.Name, device, StringComparison.OrdinalIgnoreCase))
               ?? c.Phones.FirstOrDefault(p => p.Name.Contains(device, StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolError("not_found", $"no phone \"{device}\"; linked: {string.Join(", ", c.Phones.Select(p => p.Name))}");
    }

    public async Task<ToolResult> RunAsync(JsonObject a, CancellationToken ct)
    {
        var c = Config.Current;
        var phone = Pick(c, a.Str("device"));
        var client = Client(phone);
        var tool = a.Str("tool") ?? "tools";
        if (tool is "tools" or "list")
        {
            var tools = await client.ToolsAsync(ct);
            return ToolResult.Text($"phone \"{phone.Name}\" ({phone.Url}) tools — call phone tool=<name> args={{…}}:\n" +
                                   string.Join('\n', tools.Select(t => $"{t.Name}: {FirstSentence(t.Description)}")));
        }
        var args = a["args"] as JsonObject ?? new JsonObject();
        // Long phone work (a flow, waiting for an SMS) gets the time it needs.
        var timeout = tool is "flows" ? 590_000 : tool is "wait_for" or "notifications" or "sms" or "calls" ? 120_000 : 60_000;
        var r = await client.CallAsync(tool, (JsonObject)args.DeepClone(), timeout, ct);
        // Say which device answered, so a reply is never mistaken for the PC's screen.
        if (r.Content.Count > 0 && r.Content[0].Type == "text")
        {
            var list = r.Content.ToList();
            var t = list[0].Text ?? "";
            var nl = t.IndexOf((char)10);
            // At the end of the first line: replies still start with their verdict.
            list[0] = Content.Of(nl < 0 ? t + $" · on phone {phone.Name}" : t[..nl] + $" · on phone {phone.Name}" + t[nl..]);
            r = r with { Content = list };
        }
        return r;
    }

    private static string FirstSentence(string s)
    {
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 && i < 160 ? s[..(i + 1)] : s.Length > 160 ? s[..159] + "…" : s;
    }
}
