using System.Text.Json;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Server;

/// <summary>
/// The flows tool (spec/CONTRACT.md §5.2). Records the agent's successful calls, saves them to
/// %LOCALAPPDATA%\Reach\flows\NAME.json and replays them step by step with no model in the loop. A step whose
/// element id has changed is found again by its words (and the flow is updated). The first step that doesn't go as
/// recorded stops the run and hands the screen back to the agent.
/// </summary>
public sealed class FlowRunner(Func<string, JsonObject, CancellationToken, Task<ToolResult>> call, string platform = "windows")
{
    private sealed class Recording
    {
        public required string Name;
        public string Description = "";
        public readonly JsonArray Steps = new();
    }

    private Recording? _rec;
    private readonly object _lock = new();

    public static string Dir => Path.Combine(Config.Dir, "flows");
    private static string PathOf(string name) => Path.Combine(Dir, name + ".json");
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Told about every call the agent makes; adds it to the recording if one is on.</summary>
    public void Saw(string tool, JsonObject args, ToolResult r)
    {
        lock (_lock)
        {
            if (_rec == null) return;
            foreach (var s in Flow.StepsOf(tool, args, r))
                if (_rec.Steps.Count < Flow.MaxSteps) _rec.Steps.Add(s);
        }
    }

    public async Task<ToolResult> RunAsync(JsonObject a, CancellationToken ct)
    {
        var action = a.Str("action") ?? "list";
        switch (action)
        {
            case "record_start":
            {
                var name = Flow.CleanName(a.Str("name"));
                lock (_lock) _rec = new Recording { Name = name, Description = a.Str("description") ?? "" };
                return ToolResult.Text($"recording flow \"{name}\": do the task now (act, apps, wait_for, …); observe/find are not recorded. " +
                                       "Then flows record_stop, with inputs={\"name\": \"example value you typed\"} for parts that change each time.");
            }
            case "record_stop":
            {
                Recording rec;
                lock (_lock)
                {
                    rec = _rec ?? throw new ToolError("not_recording", "no recording is on; flows record_start name=… first");
                    _rec = null;
                }
                if (a.Bool("discard") == true) return ToolResult.Text($"recording \"{rec.Name}\" thrown away");
                if (rec.Steps.Count == 0) throw new ToolError("empty", "nothing was recorded (only successful act/apps/wait_for/… calls count)");
                var inputs = Flow.MakeInputs(rec.Steps, a["inputs"] as JsonObject);
                var flow = new JsonObject
                {
                    ["name"] = rec.Name,
                    ["description"] = a.Str("description") ?? rec.Description,
                    ["platform"] = platform,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ["inputs"] = inputs,
                    ["steps"] = rec.Steps.DeepClone(),
                };
                Save(flow);
                return ToolResult.Text($"saved flow \"{rec.Name}\" · {rec.Steps.Count} steps" +
                                       (inputs.Count > 0 ? $" · inputs {string.Join(", ", inputs.Select(kv => kv.Key))}" : "") +
                                       Warnings(rec.Steps) + "\n" + Show(flow));
            }
            case "save":
            {
                // The agent writes a flow itself: steps = [{tool, args}], {{name}} marks inputs.
                var name = Flow.CleanName(a.Str("name"));
                var steps = a["steps"] as JsonArray ?? throw new ToolError("bad_args", "save needs steps: [{\"tool\": \"act\", \"args\": {…}}]");
                foreach (var s in steps)
                    if (s is not JsonObject o || o.Str("tool") is not string t || o["args"] is not JsonObject || Flow.NotRecorded.Contains(t))
                        throw new ToolError("bad_args", "each step is {\"tool\": name, \"args\": {…}} and can't be observe/find/device/flows/undo");
                var flow = new JsonObject
                {
                    ["name"] = name, ["description"] = a.Str("description") ?? "", ["platform"] = platform,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ["inputs"] = a["inputs"]?.DeepClone() ?? new JsonObject(), ["steps"] = steps.DeepClone(),
                };
                Save(flow);
                return ToolResult.Text($"saved flow \"{name}\" · {steps.Count} steps\n" + Show(flow));
            }
            case "list":
            {
                var rows = All().Select(f => $"{f.Str("name")} · {(f["steps"] as JsonArray)?.Count ?? 0} steps" +
                                             ((f["inputs"] as JsonObject)?.Count > 0 ? $" · inputs {string.Join(", ", ((JsonObject)f["inputs"]!).Select(kv => kv.Key))}" : "") +
                                             (f.Str("description") is { Length: > 0 } d ? $" · {d}" : "")).ToList();
                var rec = _rec != null ? $"\n(recording \"{_rec.Name}\" now, {_rec.Steps.Count} steps so far)" : "";
                return ToolResult.Text((rows.Count == 0 ? "no flows saved yet; flows record_start name=… to make one" : string.Join('\n', rows)) + rec);
            }
            case "show": return ToolResult.Text(Show(Load(Flow.CleanName(a.Str("name")))));
            case "delete":
            {
                var name = Flow.CleanName(a.Str("name"));
                if (!File.Exists(PathOf(name))) throw new ToolError("not_found", $"no flow \"{name}\"");
                File.Delete(PathOf(name));
                return ToolResult.Text($"deleted flow \"{name}\"");
            }
            case "run": return await Run(Load(Flow.CleanName(a.Str("name"))), a["inputs"] as JsonObject, a.Int("from_step") ?? 1, ct);
            default: throw new ToolError("bad_args", "flows actions: list, show, run, record_start, record_stop, save, delete");
        }
    }

    private async Task<ToolResult> Run(JsonObject flow, JsonObject? given, int from, CancellationToken ct)
    {
        var name = flow.Str("name");
        var steps = flow["steps"] as JsonArray ?? new JsonArray();
        var defaults = flow["inputs"] as JsonObject ?? new JsonObject();
        var lines = new List<string>();
        var healed = false;
        ToolResult? last = null;
        if (from < 1 || from > steps.Count) throw new ToolError("bad_args", $"from_step must be 1–{steps.Count}");
        for (var i = from - 1; i < steps.Count; i++)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            var step = (JsonObject)steps[i]!;
            var tool = step.Str("tool")!;
            var args = Flow.Fill((JsonObject)step["args"]!, defaults, given);
            ToolResult r;
            var note = "";
            if (Flow.ActArgs(tool, args) is { } act && act["target"] is JsonObject t && t.Str("id") is string id)
            {
                // The recorded id first; if it's gone, the element's words.
                act["target"] = id;
                r = await call(tool, args, ct);
                if (Flow.ErrorCode(r) is "stale_id" or "not_found" && t.Str("text") is string words)
                {
                    act["target"] = new JsonObject { ["text"] = words };
                    r = await call(tool, args, ct);
                    if (!r.IsError)
                    {
                        note = $" (found again by \"{words}\")";
                        if (Flow.IdIn(r.Content.FirstOrDefault()?.Text ?? "", words) is string newId && Flow.ActArgs(tool, (JsonObject)step["args"]!) is { } saved)
                        {
                            saved["target"] = new JsonObject { ["id"] = newId, ["text"] = words };
                            healed = true;
                        }
                    }
                }
            }
            else r = await call(tool, args, ct);
            last = r;
            var first = (r.Content.FirstOrDefault()?.Text ?? "").Split('\n')[0];
            if (!Flow.Good(step, r))
            {
                if (healed) Save(flow);
                var screen = await call("observe", new JsonObject(), ct);
                return new ToolResult(new[]
                {
                    Content.Of($"flow \"{name}\" stopped at step {i + 1}/{steps.Count} ({step.Str("what")}): {first}\n" +
                               (lines.Count > 0 ? string.Join('\n', lines) + "\n" : "") +
                               $"take over from here; when the screen is right, flows run name={name} from_step={i + 2} finishes the rest.\n" +
                               (screen.IsError ? "" : screen.Content[0].Text)),
                }, IsError: false);
            }
            lines.Add($"{i + 1} ok · {Clip(first.StartsWith("verdict: ") ? first[9..] : first, 120)}{note}");
        }
        if (healed) Save(flow);
        var body = last?.Content.FirstOrDefault()?.Text ?? "";
        var rest = body.Contains('\n') ? body[(body.IndexOf('\n') + 1)..] : "";
        return ToolResult.Text($"verdict: done · flow \"{name}\" ran {steps.Count - from + 1} steps with no model calls{(healed ? " (updated ids saved)" : "")}\n" +
                               string.Join('\n', lines) + (rest.Length > 0 ? "\nlast step:\n" + Clip(rest, 2_000) : ""));
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Warnings(JsonArray steps)
    {
        var shots = steps.OfType<JsonObject>().Select((s, i) => (s, i)).Where(x => x.s["args"]?["target"]?["in"]?.ToString() == "screenshot").Select(x => x.i + 1).ToList();
        return shots.Count == 0 ? "" : $"\nnote: step {string.Join(", ", shots)} tap screenshot points; they break if the window moves (prefer ids or text)";
    }

    private static string Show(JsonObject f)
    {
        var steps = (f["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select((s, i) => $"{i + 1}. {s.Str("tool")} {Clip(((JsonObject)s["args"]!).ToJsonString(), 160)}");
        var inputs = f["inputs"] as JsonObject;
        return $"flow {f.Str("name")}{(f.Str("description") is { Length: > 0 } d ? " · " + d : "")}\n" +
               (inputs?.Count > 0 ? "inputs: " + string.Join(", ", inputs.Select(kv => $"{kv.Key} (e.g. \"{kv.Value}\")")) + "\n" : "") +
               string.Join('\n', steps);
    }

    private static void Save(JsonObject flow)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathOf(flow.Str("name")!), flow.ToJsonString(Pretty));
    }

    private static JsonObject Load(string name)
    {
        if (!File.Exists(PathOf(name)))
            throw new ToolError("not_found", $"no flow \"{name}\"; saved: {string.Join(", ", All().Select(f => f.Str("name")))}");
        return JsonNode.Parse(File.ReadAllText(PathOf(name))) as JsonObject ?? throw new ToolError("broken", $"{name}.json can't be read");
    }

    private static List<JsonObject> All()
    {
        if (!Directory.Exists(Dir)) return new();
        var list = new List<JsonObject>();
        foreach (var f in Directory.GetFiles(Dir, "*.json").OrderBy(x => x))
            try { if (JsonNode.Parse(File.ReadAllText(f)) is JsonObject o) list.Add(o); } catch { }
        return list;
    }
}
