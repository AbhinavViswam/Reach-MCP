using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reach.Mcp;

namespace Reach.Core;

/// <summary>
/// Flows (spec/CONTRACT.md §5.2): a task done once, saved as steps, replayed without the model. The same file format
/// and rules as Reach for Android's Flow.kt. Plain code, tested; Server/FlowRunner.cs runs them.
/// </summary>
public static partial class Flow
{
    /// <summary>Calls that read or manage, never part of a flow.</summary>
    public static readonly HashSet<string> NotRecorded = new() { "observe", "find", "device", "doctor", "flows", "undo" };

    public const int MaxSteps = 100;

    [GeneratedRegex(@"\b(e[0-9a-z]{3,5}(?:-\d+)?) ""([^""]*)""")]
    private static partial Regex IdLabel();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex NameOk();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Slot();

    public static string CleanName(string? name)
    {
        var n = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "_").Trim('_');
        if (!NameOk().IsMatch(n)) throw new ToolError("bad_args", "give the flow a short name, e.g. send_report (letters, digits, _ and -)");
        return n;
    }

    /// <summary>The word after "verdict:" on the first line, or null for replies without one.</summary>
    public static string? VerdictOf(string firstLine)
    {
        if (!firstLine.StartsWith("verdict: ")) return null;
        var rest = firstLine[9..];
        var i = rest.IndexOf(' ');
        return i < 0 ? rest : rest[..i];
    }

    /// <summary>
    /// The steps one successful call adds to a recording. An element id gets its label next to it, so a replay can
    /// find the element by its words when the id has changed. act_batch becomes its single acts.
    /// </summary>
    public static List<JsonObject> StepsOf(string tool, JsonObject args, ToolResult r)
    {
        var list = new List<JsonObject>();
        if (r.IsError || NotRecorded.Contains(tool)) return list;
        // Another device's tool (phone on the PC, pc on the phone): the same rules for its inner call.
        if (tool is "phone" or "pc" && (args.Str("tool") is null or "tools" or "list" || NotRecorded.Contains(args.Str("tool")!))) return list;
        var text = r.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        if (tool == "act_batch" && args["actions"] is JsonArray acts)
        {
            var lines = text.Split('\n').Where(l => l.StartsWith("step ")).ToList();
            for (var i = 0; i < acts.Count && i < lines.Count; i++)
            {
                if (acts[i] is not JsonObject one) continue;
                var line = lines[i];
                if (line.Contains(": error ")) break;
                var what = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                list.Add(Step("act", one, VerdictOf("verdict: " + what), what));
            }
            return list;
        }
        var first = text.Split('\n')[0];
        list.Add(Step(tool, args, VerdictOf(first), first.StartsWith("verdict: ") ? first[9..] : first));
        return list;
    }

    /// <summary>The id a reply names for the element labelled [words] ("click e7ff \"Send\"" → e7ff).</summary>
    public static string? IdIn(string reply, string words) =>
        IdLabel().Matches(reply.Split((char)10)[0]).FirstOrDefault(m => m.Groups[2].Value == words)?.Groups[1].Value;

    private static JsonObject Step(string tool, JsonObject args, string? verdict, string what)
    {
        var a = (JsonObject)args.DeepClone();
        if (ActArgs(tool, a) is { } act && act["target"] is JsonValue v && v.TryGetValue<string>(out var id))
        {
            var m = IdLabel().Matches(what).FirstOrDefault(x => x.Groups[1].Value == id);
            act["target"] = m != null && m.Groups[2].Value.Length > 0
                ? new JsonObject { ["id"] = id, ["text"] = m.Groups[2].Value }
                : new JsonObject { ["id"] = id };
        }
        var s = new JsonObject { ["tool"] = tool, ["args"] = a, ["what"] = what.Length > 160 ? what[..160] : what };
        if (verdict != null) s["verdict"] = verdict;
        return s;
    }

    /// <summary>The act arguments inside a step: the step's own for act, the inner ones for phone/pc tool=act.</summary>
    public static JsonObject? ActArgs(string tool, JsonObject args) =>
        tool == "act" ? args : tool is "phone" or "pc" && args.Str("tool") == "act" ? args["args"] as JsonObject : null;

    /// <summary>
    /// Turns chosen values into inputs: every string argument equal to (or containing) an input's example value gets
    /// {{name}} instead. Returns the inputs that were found in at least one step.
    /// </summary>
    public static JsonObject MakeInputs(JsonArray steps, JsonObject? wanted)
    {
        var found = new JsonObject();
        if (wanted == null) return found;
        foreach (var (name, val) in wanted)
        {
            if (val is not JsonValue jv || !jv.TryGetValue<string>(out var example) || example.Length == 0) continue;
            var hit = false;
            foreach (var s in steps.OfType<JsonObject>())
                if (s["args"] is JsonObject a) hit |= Replace(a, example, "{{" + name + "}}");
            if (hit) found[name] = example;
        }
        return found;
    }

    private static bool Replace(JsonNode node, string example, string slot)
    {
        var hit = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList())
                {
                    var v = o[k];
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Contains(example) && k != "id")
                    {
                        o[k] = s.Replace(example, slot);
                        hit = true;
                    }
                    else if (v != null) hit |= Replace(v, example, slot);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Contains(example)) { arr[i] = s.Replace(example, slot); hit = true; }
                    else if (arr[i] != null) hit |= Replace(arr[i]!, example, slot);
                }
                break;
        }
        return hit;
    }

    /// <summary>The step's arguments with {{name}} filled from [given], else the flow's example value.</summary>
    public static JsonObject Fill(JsonObject args, JsonObject defaults, JsonObject? given)
    {
        var json = args.ToJsonString();
        var missing = new List<string>();
        var filled = Slot().Replace(json, m =>
        {
            var n = m.Groups[1].Value;
            var v = given?[n] is JsonValue g && g.TryGetValue<string>(out var gs) ? gs
                : given?[n] is JsonNode other ? other.ToJsonString().Trim('"')
                : defaults[n] is JsonValue d && d.TryGetValue<string>(out var ds) ? ds : null;
            if (v == null) { missing.Add(n); return m.Value; }
            // Inside a JSON string: escape it the JSON way, without the quotes.
            return JsonSerializer.Serialize(v)[1..^1];
        });
        if (missing.Count > 0) throw new ToolError("bad_args", $"the flow needs inputs: {string.Join(", ", missing.Distinct())}");
        return (JsonObject)JsonNode.Parse(filled)!;
    }

    /// <summary>
    /// Is the replayed step as good as the recorded one? Errors and popups never are; no_effect is fine only when the
    /// recording also had it (or the step is marked optional).
    /// </summary>
    public static bool Good(JsonObject step, ToolResult r)
    {
        if (r.IsError) return false;
        var now = VerdictOf((r.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "").Split('\n')[0]);
        if (now == null || now is "done" or "unknown") return true;
        if (now is "blocked_by_popup" or "needs_foreground") return false;
        return step.Str("verdict") == now || step["optional"]?.GetValue<bool>() == true;
    }

    public static string ErrorCode(ToolResult r)
    {
        var t = r.Content.FirstOrDefault()?.Text ?? "";
        return t.StartsWith("error: ") ? t[7..].Split(' ', '\n')[0] : "";
    }
}
