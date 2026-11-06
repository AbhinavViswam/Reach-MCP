using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Deep;

/// <summary>
/// Page elements as Reach elements (spec/CONTRACT.md §2), from what the in-page script collects. Plain code, tested.
/// </summary>
public static class PageModel
{
    public sealed record Item(int Index, Element Element);

    public static List<Item> Elements(string host, JsonArray items)
    {
        var raws = new List<(int, RawElement)>();
        foreach (var n in items)
        {
            if (n is not JsonObject o) continue;
            var tag = o.Str("tag") ?? "";
            var type = o.Str("type") ?? "";
            var role = o.Str("role") ?? "";
            Role? r = (tag, type, role) switch
            {
                (_, "password", _) => Role.Password,
                (_, _, "button") or ("button", _, _) or ("summary", _, _) => Role.Button,
                (_, "submit", _) or (_, "button", _) or (_, "reset", _) => Role.Button,
                (_, "checkbox", _) or (_, _, "checkbox") => Role.Checkbox,
                (_, "radio", _) or (_, _, "radio") => Role.Radio,
                (_, _, "switch") => Role.Switch,
                (_, _, "tab") => Role.Tab,
                (_, _, "menuitem") => Role.Menu,
                (_, _, "option") => Role.Item,
                ("select", _, _) => Role.Button,
                ("input", _, _) or ("textarea", _, _) or (_, _, "textbox") or (_, _, "searchbox") or ("editable", _, _) => Role.Field,
                ("a", _, _) or (_, _, "link") => Role.Link,
                ("h1", _, _) or ("h2", _, _) or ("h3", _, _) => Role.Text,
                _ => null,
            };
            if (r == null) continue;
            var id = o.Str("id");
            // Ids made by frameworks ("radix-:r3:", "ember1234") change on every load: no good for stable IDs.
            if (id != null && (id.Count(char.IsDigit) >= 3 || id.Contains(':'))) id = null;
            var box = new Box((int)(o.Int("x") ?? 0), (int)(o.Int("y") ?? 0), (int)((o.Int("x") ?? 0) + (o.Int("w") ?? 0)), (int)((o.Int("y") ?? 0) + (o.Int("h") ?? 0)));
            raws.Add((o.Int("i") ?? -1, new RawElement(
                Role: r.Value,
                Label: o.Str("label") ?? "",
                Value: r == Role.Password ? null : r is Role.Field or Role.Button && tag != "button" ? o.Str("value") : null,
                ResourceId: id,
                ClassName: "html." + tag,
                Bounds: box,
                Checked: o["checked"] is JsonValue c && c.TryGetValue<bool>(out var cb) && r is Role.Checkbox or Role.Radio or Role.Switch ? (bool?)cb : null,
                Enabled: o.Bool("disabled") != true,
                Focused: o.Bool("focused") == true)));
        }
        var els = Ids.Assign(host, raws.Select(x => x.Item2));
        return els.Select((e, k) => new Item(raws[k].Item1, e)).ToList();
    }
}

/// <summary>
/// Edge or Chrome through the DevTools protocol (CDP): read pages, click and type with real browser input events,
/// all without the user's mouse and even in a background tab. Needs a browser started with a debugging port;
/// action start opens one with its own Reach profile (Chrome and Edge don't allow it on the everyday profile).
/// </summary>
internal sealed class BrowserTool(Config config)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ScreenState _state = new();
    private string? _tab;
    private Dictionary<string, int> _index = new();
    private int Port => config.BrowserPort;

    public ToolResult Run(JsonObject a)
    {
        var action = a.Str("action") ?? throw new ToolError("bad_args", "action is needed");
        if (action == "start") return Start(a);
        var tabs = Tabs();
        switch (action)
        {
            case "tabs":
                return ToolResult.Text(tabs.Count == 0 ? "no tabs" : string.Join('\n', tabs.Select(t =>
                    $"{Short(t.Id)} \"{ViewText.Clip(t.Title, 70)}\" · {ViewText.Clip(t.Url, 90)}{(t.Id == _tab ? " (working)" : "")}{(Privacy.IsBlocked(null, t.Title) ? " (blocked by Reach privacy rules)" : "")}")));
            case "open":
            {
                var url = a.Str("url") ?? throw new ToolError("bad_args", "open needs url");
                if (a.Bool("new_tab") == true || tabs.Count == 0)
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{Port}/json/new?{Uri.EscapeDataString(url)}");
                    var j = JsonNode.Parse(Http.Send(req).Content.ReadAsStringAsync().Result)!.AsObject();
                    _tab = j.Str("id");
                }
                else
                {
                    _tab ??= tabs[0].Id;
                    Cdp(Ws(_tab!), "Page.navigate", new JsonObject { ["url"] = url });
                }
                WaitLoaded();
                return ToolResult.Text("verdict: done · opened " + url + "\n" + View(null));
            }
            case "switch":
            {
                var t = Match(tabs, a.Str("tab") ?? throw new ToolError("bad_args", "switch needs tab (id or words from its title)"));
                _tab = t.Id;
                Http.GetAsync($"http://127.0.0.1:{Port}/json/activate/{t.Id}").Wait();
                return ToolResult.Text("verdict: done · working in this tab\n" + View(null));
            }
            case "close":
            {
                var t = a.Str("tab") is string q ? Match(tabs, q) : tabs.FirstOrDefault(x => x.Id == _tab) ?? throw new ToolError("bad_args", "close needs tab");
                Http.GetAsync($"http://127.0.0.1:{Port}/json/close/{t.Id}").Wait();
                if (_tab == t.Id) _tab = null;
                return ToolResult.Text($"verdict: done · closed \"{t.Title}\"");
            }
            case "read":
                return ToolResult.Text(View(a.Int("since"), a.Bool("bounds") == true));
            case "text":
            {
                var page = Eval("(() => ({ title: document.title, url: location.href, text: (document.body ? document.body.innerText : '').slice(0, 15000) }))()");
                var (t, masked) = Mask.Apply(page.Str("text") ?? "");
                return ToolResult.Text($"page \"{page.Str("title")}\" · {page.Str("url")}\n{ViewText.Fence}\n{t}" + (masked ? "\n" + Core.Mask.Note : ""));
            }
            case "click" or "type" or "key" or "scroll" or "back" or "forward" or "reload":
                return ToolResult.Text(Act(action, a));
            case "screenshot":
            {
                var shot = Cdp(Ws(WorkingTab()), "Page.captureScreenshot", new JsonObject { ["format"] = "png" });
                var bytes = Convert.FromBase64String(shot.Str("data") ?? "");
                using var ms = new MemoryStream(bytes);
                using var bmp = new System.Drawing.Bitmap(ms);
                var s = Worker.Capture.Encode(bmp, 0, 0);
                return new ToolResult(new[] { Content.Of($"page screenshot {s.Width}×{s.Height} (page pixels; act by element id)"), Content.Image(s.Base64, "image/jpeg", s.Width, s.Height) });
            }
            default:
                throw new ToolError("bad_args", "actions: start, tabs, open, switch, close, read, text, click, type, key, scroll, back, forward, reload, screenshot");
        }
    }

    // -------------------------------------------------------------------------------------------- browser and tabs

    private sealed record Tab(string Id, string Title, string Url, string Ws);

    private ToolResult Start(JsonObject a)
    {
        if (Reachable()) return ToolResult.Text($"verdict: done · a browser already answers on port {Port}");
        var which = (a.Str("browser") ?? "edge").ToLowerInvariant();
        var exe = which == "chrome"
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe") }.FirstOrDefault(File.Exists)
            : new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe") }.FirstOrDefault(File.Exists);
        if (exe == null) throw new ToolError("not_found", $"{which} isn't installed");
        var profile = Path.Combine(Config.Dir, "browser-" + which);
        Process.Start(new ProcessStartInfo(exe, $"--remote-debugging-port={Port} --user-data-dir=\"{profile}\" --no-first-run about:blank") { UseShellExecute = false });
        for (var i = 0; i < 40 && !Reachable(); i++) Thread.Sleep(250);
        if (!Reachable()) throw new ToolError("not_started", $"{which} started but doesn't answer on port {Port}; is another copy using that profile?");
        return ToolResult.Text($"verdict: done · {which} started with its own Reach profile (sign in there once if sites need it); port {Port}");
    }

    private bool Reachable()
    {
        try { return Http.GetAsync($"http://127.0.0.1:{Port}/json/version").Result.IsSuccessStatusCode; } catch { return false; }
    }

    private List<Tab> Tabs()
    {
        string json;
        try { json = Http.GetStringAsync($"http://127.0.0.1:{Port}/json/list").Result; }
        catch
        {
            throw new ToolError("no_browser", $"no browser answers on port {Port}. Use action start (opens Edge with its own Reach profile), " +
                                              $"or start Edge/Chrome yourself with --remote-debugging-port={Port} --user-data-dir=<a folder>");
        }
        return JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().Where(t => t.Str("type") == "page")
            .Select(t => new Tab(t.Str("id")!, t.Str("title") ?? "", t.Str("url") ?? "", t.Str("webSocketDebuggerUrl") ?? "")).ToList();
    }

    private static string Short(string id) => "t" + id[..Math.Min(6, id.Length)].ToLowerInvariant();

    private static Tab Match(List<Tab> tabs, string q) =>
        tabs.FirstOrDefault(t => Short(t.Id) == q || t.Id == q)
        ?? tabs.FirstOrDefault(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
        ?? throw new ToolError("not_found", $"no tab matches \"{q}\"; action tabs lists them");

    private string WorkingTab()
    {
        var tabs = Tabs();
        if (_tab != null && tabs.Any(t => t.Id == _tab)) return _tab;
        _tab = tabs.FirstOrDefault()?.Id ?? throw new ToolError("not_found", "no tab open; action open with a url");
        return _tab;
    }

    private string Ws(string id) => Tabs().FirstOrDefault(t => t.Id == id)?.Ws is { Length: > 0 } w ? w
        : throw new ToolError("busy", "the tab is attached to DevTools elsewhere; close that DevTools window");

    // -------------------------------------------------------------------------------------------- CDP

    private static int _msg;

    /// <summary>One CDP command on its own connection; the reply's result (10 s limit).</summary>
    private static JsonObject Cdp(string ws, string method, JsonObject? p = null)
    {
        using var sock = new ClientWebSocket();
        using var cts = new CancellationTokenSource(10_000);
        sock.ConnectAsync(new Uri(ws), cts.Token).GetAwaiter().GetResult();
        var id = Interlocked.Increment(ref _msg);
        var req = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();
        sock.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();
        var buf = new byte[1 << 16];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = sock.ReceiveAsync(buf, cts.Token).GetAwaiter().GetResult();
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            var msg = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
            if (msg?["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
            {
                try { sock.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
                if (msg["error"] is JsonObject err) throw new ToolError("browser_error", err.Str("message") ?? "CDP error");
                return msg["result"] as JsonObject ?? new JsonObject();
            }
        }
    }

    /// <summary>Runs [js] in the working tab and returns its value (an object).</summary>
    private JsonObject Eval(string js)
    {
        var r = Cdp(Ws(WorkingTab()), "Runtime.evaluate", new JsonObject { ["expression"] = js, ["returnByValue"] = true, ["awaitPromise"] = true });
        if (r["exceptionDetails"] is JsonObject ex) throw new ToolError("browser_error", ex["exception"]?["description"]?.ToString() ?? "script error");
        var v = r["result"]?["value"];
        return v switch
        {
            JsonObject o => o,
            JsonValue s when s.TryGetValue<string>(out var str) && str.StartsWith('{') => JsonNode.Parse(str)!.AsObject(),
            _ => new JsonObject { ["value"] = v?.DeepClone() },
        };
    }

    private void WaitLoaded()
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(300);
        while (sw.ElapsedMilliseconds < 8_000)
        {
            try { if (Eval("({ value: document.readyState })").Str("value") == "complete") break; } catch { }
            Thread.Sleep(200);
        }
    }

    // -------------------------------------------------------------------------------------------- reading

    /// <summary>
    /// Marks every element a person could use with data-reach="n" and returns them, up to 3 screens down.
    /// Password values are never read.
    /// </summary>
    private const string Collect = """
        (() => {
          const out = []; let i = 0;
          document.querySelectorAll('[data-reach]').forEach(e => e.removeAttribute('data-reach'));
          const sel = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],' +
                      '[role=tab],[role=menuitem],[role=option],[role=switch],[role=textbox],[role=searchbox],[contenteditable=""],[contenteditable=true],h1,h2,h3';
          for (const el of document.querySelectorAll(sel)) {
            const r = el.getBoundingClientRect();
            if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.top > innerHeight * 3) continue;
            const st = getComputedStyle(el);
            if (st.visibility === 'hidden' || st.display === 'none' || st.opacity === '0') continue;
            const tag = el.isContentEditable && !['input','textarea'].includes(el.tagName.toLowerCase()) ? 'editable' : el.tagName.toLowerCase();
            const type = (el.getAttribute('type') || '').toLowerCase();
            const pwd = type === 'password';
            const lab = el.labels && el.labels[0] ? el.labels[0].innerText : '';
            let label = el.getAttribute('aria-label') || lab || el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('alt') || '';
            if (!label && !['input','textarea','editable'].includes(tag)) label = el.innerText || el.value || '';
            label = String(label).replace(/\s+/g, ' ').trim().slice(0, 120);
            let value = null;
            if (!pwd) value = tag === 'select' ? (el.selectedOptions[0] ? el.selectedOptions[0].text : '') : tag === 'editable' ? el.innerText.slice(0, 500) : (el.value ?? null);
            el.setAttribute('data-reach', String(i));
            out.push({ i, tag, type, role: (el.getAttribute('role') || '').toLowerCase(), label, value, id: el.id || el.getAttribute('name') || '',
                       checked: (type === 'checkbox' || type === 'radio') ? el.checked : (el.getAttribute('aria-checked') === 'true' ? true : el.getAttribute('aria-checked') === 'false' ? false : null),
                       disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true', focused: document.activeElement === el,
                       x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) });
            if (++i >= 300) break;
          }
          return JSON.stringify({ title: document.title, url: location.href, w: innerWidth, h: innerHeight, scrollY: Math.round(scrollY), items: out });
        })()
        """;

    private (Snapshot Snap, Dictionary<string, int> Index) ReadPage()
    {
        var tab = WorkingTab();
        var page = Eval(Collect);
        var url = page.Str("url") ?? "";
        var title = page.Str("title") ?? "";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var viewport = new Box(0, 0, page.Int("w") ?? 0, page.Int("h") ?? 0);
        var header = $"browser tab {Short(tab)} · {ViewText.Clip(url, 100)}";
        if (Privacy.IsBlocked(null, title) || Privacy.IsBlocked(null, host))
            return (new Snapshot(host, title, Short(tab), viewport, header, Array.Empty<Element>(), BlockedApp: host), new());
        var items = PageModel.Elements(host, page["items"] as JsonArray ?? new JsonArray());
        var snap = _state.Store(new Snapshot(host, title, Short(tab), viewport, header, items.Select(i => i.Element).ToList()));
        var index = items.ToDictionary(i => i.Element.Id, i => i.Index);
        _index = index;
        return (snap, index);
    }

    private string View(int? since, bool bounds = false)
    {
        var (snap, _) = ReadPage();
        var b = since is int v ? _state.Get(v) : null;
        if (b == null || b.Version == snap.Version) return b?.Version == snap.Version ? $"{ViewText.Header(snap)}\nno changes since v{since}" : ViewText.Full(snap, bounds);
        var d = Diff.Of(b, snap);
        return d.MostlyNew ? ViewText.Full(snap, bounds) : $"{ViewText.Header(snap)}\n{ViewText.Fence}\n{d.Text(bounds)}";
    }

    // -------------------------------------------------------------------------------------------- acting

    private string Act(string action, JsonObject a)
    {
        var (before, index) = ReadPage();
        if (before.BlockedApp != null) throw new ToolError("blocked_app", $"{before.BlockedApp} is blocked by Reach privacy rules");
        var ws = Ws(WorkingTab());
        Element? target = null;
        string what;
        if (a["target"] != null)
        {
            target = Resolve(before, a["target"]);
            if (!index.ContainsKey(target.Id)) throw new ToolError("stale_id", "that element is gone; read again");
        }
        switch (action)
        {
            case "click":
            {
                if (target == null) throw new ToolError("bad_args", "click needs target");
                what = $"click {target.Id} \"{target.Label}\"";
                var (x, y) = Centre(index[target.Id]);
                Mouse(ws, x, y);
                break;
            }
            case "type":
            {
                var text = a.Str("text") ?? throw new ToolError("bad_args", "type needs text");
                if (target?.Role == Role.Password) throw new ToolError("blocked", "Reach does not type into password fields; ask the user");
                var append = a.Str("mode") == "append";
                var n = target != null ? index[target.Id] : -1;
                // Focus the box (or keep the focused one) and select what's in it, then type over it with real key input.
                var ok = Eval($$"""
                    (() => { const el = {{(n >= 0 ? $"document.querySelector('[data-reach=\"{n}\"]')" : "document.activeElement")}};
                      if (!el) return { value: false };
                      el.scrollIntoView({ block: 'center' }); el.focus();
                      if ({{(append ? "false" : "true")}}) { if (el.select) el.select(); else document.execCommand('selectAll'); }
                      else if (el.setSelectionRange) { const l = (el.value || '').length; el.setSelectionRange(l, l); }
                      return { value: true }; })()
                    """).Bool("value");
                if (ok != true) throw new ToolError("not_found", "no text box to type into; give a target");
                Cdp(ws, "Input.insertText", new JsonObject { ["text"] = text });
                if (a.Bool("submit") == true) Key(ws, "Enter");
                what = $"type \"{ViewText.Clip(text, 40)}\"" + (target != null ? $" into {target.Id} \"{target.Label}\"" : "") + (a.Bool("submit") == true ? " + Enter" : "");
                break;
            }
            case "key":
            {
                var k = a.Str("key") ?? throw new ToolError("bad_args", "key needs key: Enter, Tab, Escape, ArrowDown…");
                Key(ws, k);
                what = $"press {k}";
                break;
            }
            case "scroll":
            {
                var dy = (a.Str("direction") ?? "down") switch { "up" => -0.8, _ => 0.8 };
                Eval($"(() => {{ scrollBy(0, innerHeight * {dy.ToString(System.Globalization.CultureInfo.InvariantCulture)}); return {{ value: true }}; }})()");
                what = $"scroll {a.Str("direction") ?? "down"}";
                break;
            }
            default:
            {
                Eval(action switch { "back" => "(() => { history.back(); return {}; })()", "forward" => "(() => { history.forward(); return {}; })()", _ => "(() => { location.reload(); return {}; })()" });
                what = action;
                break;
            }
        }
        Thread.Sleep(250);
        WaitLoaded();
        var (after, _) = ReadPage();
        var diff = Diff.Of(before, after);
        var expect = action == "type" && a.Bool("submit") != true && target?.Role == Role.Field
            ? (a.Str("mode") == "append" ? (target.Value ?? "") + a.Str("text") : a.Str("text")) : null;
        var verdict = Verdicts.Judge(action == "type" ? "type" : action, diff, 0, target?.Id, expect);
        var body = diff.MostlyNew ? ViewText.Full(after) : $"{ViewText.Fence}\n{diff.Text()}";
        return $"verdict: {verdict.Word()} · {what} (browser input, the user's mouse untouched)\n{body}";
    }

    private Element Resolve(Snapshot s, JsonNode? t)
    {
        var id = t is JsonValue v && v.TryGetValue<string>(out var str) ? str : (t as JsonObject)?.Str("id");
        if (id != null)
        {
            if (s.ById.TryGetValue(id, out var e)) return e;
            var old = _state.Known(id) ?? throw new ToolError("stale_id", $"{id} was never seen; read the page first");
            return Ids.Refind(old, s) ?? throw new ToolError("stale_id", $"{id} is not on the page any more; read again");
        }
        var text = (t as JsonObject)?.Str("text") ?? throw new ToolError("bad_args", "target must be an element id or {\"text\": …}");
        var n = Ids.Normalize(text);
        return s.Elements.FirstOrDefault(e => Ids.Normalize(e.Label) == n && e.Role.Pressable())
               ?? s.Elements.FirstOrDefault(e => Ids.Normalize(e.Label).Contains(n))
               ?? throw new ToolError("not_found", $"nothing labelled \"{text}\" on the page");
    }

    private (double X, double Y) Centre(int n)
    {
        var r = Eval($$"""
            (() => { const el = document.querySelector('[data-reach="{{n}}"]'); if (!el) return { ok: false };
              el.scrollIntoView({ block: 'center', inline: 'center' }); const b = el.getBoundingClientRect();
              return { ok: true, x: b.left + b.width / 2, y: b.top + b.height / 2 }; })()
            """);
        if (r.Bool("ok") != true) throw new ToolError("stale_id", "that element is gone; read again");
        return (r["x"]!.GetValue<double>(), r["y"]!.GetValue<double>());
    }

    /// <summary>A real (trusted) click: pages can't tell it from a person's.</summary>
    private static void Mouse(string ws, double x, double y)
    {
        Cdp(ws, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
        Cdp(ws, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
        Cdp(ws, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
    }

    private static readonly Dictionary<string, (string Code, int Vk, string? Text)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = ("Enter", 13, "\r"), ["Tab"] = ("Tab", 9, null), ["Escape"] = ("Escape", 27, null), ["Backspace"] = ("Backspace", 8, null),
        ["ArrowDown"] = ("ArrowDown", 40, null), ["ArrowUp"] = ("ArrowUp", 38, null), ["ArrowLeft"] = ("ArrowLeft", 37, null),
        ["ArrowRight"] = ("ArrowRight", 39, null), ["PageDown"] = ("PageDown", 34, null), ["PageUp"] = ("PageUp", 33, null),
        ["Home"] = ("Home", 36, null), ["End"] = ("End", 35, null), ["Delete"] = ("Delete", 46, null), ["Space"] = ("Space", 32, " "),
    };

    private static void Key(string ws, string key)
    {
        var k = Keys.TryGetValue(key, out var v) ? (Key: Keys.Keys.First(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)), v.Code, v.Vk, v.Text)
            : throw new ToolError("bad_args", $"key must be one of {string.Join(", ", Keys.Keys)}");
        var down = new JsonObject { ["type"] = k.Text != null ? "keyDown" : "rawKeyDown", ["key"] = k.Key == "Space" ? " " : k.Key, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.Vk };
        if (k.Text != null) down["text"] = k.Text;
        Cdp(ws, "Input.dispatchKeyEvent", down);
        Cdp(ws, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = k.Key == "Space" ? " " : k.Key, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.Vk });
    }
}
