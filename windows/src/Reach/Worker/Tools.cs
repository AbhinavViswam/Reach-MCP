using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Interop.UIAutomationClient;
using Reach.Core;
using Reach.Mcp;
using static Interop.UIAutomationClient.UIA_PatternIds;

namespace Reach.Worker;

/// <summary>The core tools (spec/CONTRACT.md §5), run inside the UI worker one call at a time.</summary>
internal sealed class Tools(bool backgroundOnly)
{
    private readonly ScreenState _state = new();
    private IntPtr _target;
    private Capture.Shot? _lastShot;

    public ToolResult Run(string tool, JsonObject a) => tool switch
    {
        "observe" => Observe(a),
        "act" => ToolResult.Text(Act(a).Text),
        "act_batch" => ActBatch(a),
        "wait_for" => WaitFor(a),
        "find" => Find(a),
        "apps" => Apps(a),
        "device" => Device(),
        // Test hook (tests/WatchdogTest): a call that never answers, like a frozen app.
        "__hang" when Environment.GetEnvironmentVariable("REACH_TEST_HOOKS") == "1" => Hang(),
        _ => throw new ToolError("not_found", $"no tool '{tool}'"),
    };

    private static ToolResult Hang()
    {
        Thread.Sleep(Timeout.Infinite);
        return ToolResult.Text("never");
    }

    // ============================================================================================ working window

    /// <summary>The window Reach works in: the last one opened or switched to, else the front window.</summary>
    private IntPtr Working()
    {
        if (_target != IntPtr.Zero && Native.IsWindow(_target) && Native.IsWindowVisible(_target)) return _target;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) throw new ToolError("not_found", "no window is in front; apps list, then observe window=…");
        _target = Native.GetAncestor(fg, Native.GA_ROOT);
        return _target;
    }

    /// <summary>
    /// The window whose last look needed OCR: later reads there include it too, so OCR elements' IDs keep working.
    /// </summary>
    private IntPtr _ocrWindow;

    /// <summary>
    /// Reads the working window and stores the view. OCR is added when asked, when UI Automation shows too little
    /// (games, canvas, apps without accessibility), or when this window needed it before.
    /// </summary>
    private Live Read(bool forceOcr = false)
    {
        var live = Uia.Read(Working());
        var s = live.Snap;
        var titleBar = s.Window.Top + 48 * Desktop.ScalePercent(live.Hwnd) / 100;
        var want = s.BlockedApp == null && Ocr.Available && (forceOcr || Vision.Thin(s.Elements, titleBar) || _ocrWindow == live.Hwnd);
        _ocrWindow = want ? live.Hwnd : (_ocrWindow == live.Hwnd ? IntPtr.Zero : _ocrWindow);
        if (want) live = WithOcr(live);
        return live with { Snap = _state.Store(live.Snap) };
    }

    private static Live WithOcr(Live tree)
    {
        List<OcrLine> lines;
        try
        {
            using var f = Capture.WindowFrame(tree.Hwnd);
            lines = Ocr.Lines(f.Bitmap, f.OriginX, f.OriginY);
        }
        catch (ToolError e) { Console.Error.WriteLine("ocr skipped: " + e.Hint); return tree; }
        var extra = Vision.Extra(tree.Snap.Elements, lines);
        var els = extra.Count == 0 ? tree.Snap.Elements : Ids.Assign(tree.Snap.App, tree.Snap.Elements.Select(e => e.Raw).Concat(extra));
        return tree with { Snap = tree.Snap with { Elements = els, Ocr = true } };
    }

    private string Render(Snapshot now, int? since, bool bounds = false)
    {
        var b = since is int v ? _state.Get(v) : null;
        if (b == null) return ViewText.Full(now, bounds);
        if (b.Version == now.Version) return $"{ViewText.Header(now)}\nno changes since v{since}";
        var d = Diff.Of(b, now);
        return d.MostlyNew ? ViewText.Full(now, bounds) : $"{ViewText.Header(now)}\n{ViewText.Fence}\n{d.Text(bounds)}";
    }

    // ============================================================================================ observe

    private ToolResult Observe(JsonObject a)
    {
        if (a.Str("window") is string q)
        {
            var w = Desktop.Match(q) ?? throw new ToolError("not_found", $"no open window matches \"{q}\"; apps list shows them");
            _target = w.Handle;
        }
        var mode = a.Str("mode") ?? "tree";
        var live = Read(forceOcr: mode == "ocr");
        if (mode is "screenshot" or "marks")
        {
            if (live.Snap.BlockedApp != null) throw new ToolError("blocked_app", $"{live.Snap.BlockedApp} is blocked by Reach privacy rules; no screenshot");
            if (mode == "marks")
            {
                var marked = Vision.Markable(live.Snap.Elements, live.Snap.Window.Area);
                using var f = Capture.WindowFrame(live.Hwnd);
                using var drawn = Ocr.Marks(f.Bitmap, f.OriginX, f.OriginY, marked, Desktop.ScalePercent(live.Hwnd));
                var ms = Capture.Encode(drawn, f.OriginX, f.OriginY);
                _lastShot = ms;
                var list = string.Join('\n', marked.Select((e, i) => $"{i + 1} = {ViewText.Line(e)}"));
                return new ToolResult(new[]
                {
                    Content.Of($"{ViewText.Header(live.Snap)}\nmarks: box number = element (act with its id)\n{ViewText.Fence}\n{list}"),
                    Content.Image(ms.Base64, "image/jpeg", ms.Width, ms.Height),
                });
            }
            Capture.Shot shot;
            string area;
            if (a["region"] is System.Text.Json.Nodes.JsonObject r)
            {
                var x = r.Int("x") ?? 0;
                var y = r.Int("y") ?? 0;
                var box = new Box(x, y, x + (r.Int("width") ?? 0), y + (r.Int("height") ?? 0));
                using var f = Capture.AreaFrame(box);
                shot = Capture.Encode(f.Bitmap, f.OriginX, f.OriginY);
                area = $"region {box.Left},{box.Top} {box.Width}×{box.Height}";
            }
            else
            {
                shot = a.Bool("screen") == true ? Capture.Screen() : Capture.Window(live.Hwnd);
                area = $"the area at {shot.OriginX},{shot.OriginY}";
            }
            _lastShot = shot;
            var note = $"screenshot {shot.Width}×{shot.Height} of {area} · to click a point from this image use target {{\"x\":…, \"y\":…, \"in\":\"screenshot\"}}";
            return new ToolResult(new[] { Content.Of($"{ViewText.Header(live.Snap)}\n{note}"), Content.Image(shot.Base64, "image/jpeg", shot.Width, shot.Height) });
        }
        return ToolResult.Text(Render(live.Snap, a.Int("since"), a.Bool("bounds") == true));
    }

    // ============================================================================================ targets

    private sealed record Target(Element? El, IUIAutomationElement? Node, IntPtr Owner, int X, int Y, string? Note)
    {
        public string Describe() => El != null ? $"{El.Id} \"{El.Label}\"" : $"point {X},{Y}";
    }

    private Target Resolve(Live live, JsonNode? t)
    {
        switch (t)
        {
            case null: throw new ToolError("bad_args", "target is needed: an element id like \"e4k2\", {\"text\": \"…\"} or {\"x\": …, \"y\": …}");
            case JsonValue v when v.TryGetValue<string>(out var id): return ById(live, id);
            case JsonObject o:
                if (o.Str("id") is string i2) return ById(live, i2);
                if (o.Str("text") is string text) return ByText(live, text);
                if (o.Int("x") is int x && o.Int("y") is int y)
                {
                    if (o.Str("in") == "screenshot")
                    {
                        var s = _lastShot ?? throw new ToolError("bad_args", "no screenshot taken yet");
                        x = s.OriginX + (int)Math.Round(x / s.Scale);
                        y = s.OriginY + (int)Math.Round(y / s.Scale);
                    }
                    var under = live.Snap.Elements.Where(e => e.Bounds.Contains(x, y)).MinBy(e => e.Bounds.Area);
                    return new Target(under, under != null ? live.Nodes.GetValueOrDefault(under.Id) : null,
                        under != null ? live.Owner.GetValueOrDefault(under.Id, live.Hwnd) : live.Hwnd, x, y, null);
                }
                throw new ToolError("bad_args", "target object needs id, text, or x and y");
            default: throw new ToolError("bad_args", "target must be an id string or an object");
        }
    }

    /// <summary>OCR elements have no UI Automation element behind them: they're acted on by position.</summary>
    private Target Of(Live live, Element e, string? note) =>
        new(e, live.Nodes.GetValueOrDefault(e.Id), live.Owner.GetValueOrDefault(e.Id, live.Hwnd), e.Bounds.Cx, e.Bounds.Cy, note);

    private Target ById(Live live, string id)
    {
        if (live.Snap.ById.TryGetValue(id, out var e)) return Of(live, e, null);
        var old = _state.Known(id) ?? throw new ToolError("stale_id", $"{id} was never seen; call observe for current ids");
        var now = Ids.Refind(old, live.Snap)
                  ?? throw new ToolError("stale_id", $"{id} ({old.Role.Word()} \"{old.Label}\") is not in the window any more; observe again");
        return Of(live, now, $"refound {id} → {now.Id}");
    }

    private Target ByText(Live live, string text)
    {
        var t = Ids.Normalize(text);
        var exact = live.Snap.Elements.Where(e => Ids.Normalize(e.Label) == t).ToList();
        var pool = exact.Count > 0 ? exact : live.Snap.Elements.Where(e => Ids.Normalize(e.Label).Contains(t)).ToList();
        if (pool.Count == 0) throw new ToolError("not_found", $"nothing labelled \"{text}\" in the window; try find with scroll=true");
        var best = pool.FirstOrDefault(e => e.Role.Pressable()) ?? pool[0];
        return Of(live, best, pool.Count > 1 ? $"{pool.Count} matches for \"{text}\", used {best.Id}" : null);
    }

    // ============================================================================================ checked actions

    private sealed record Outcome(Verdict Verdict, string Text, Snapshot After);

    /// <summary>Waits for the window to settle: two reads in a row that look the same (max 2.5 s).</summary>
    private Live Settle(IntPtr beforeFront)
    {
        Thread.Sleep(150);
        // A new window in front (a link opened Settings, an app opened a window): work in it from now on.
        var fg = Native.GetForegroundWindow();
        if (fg != beforeFront && fg != IntPtr.Zero)
        {
            var root = Native.GetAncestor(fg, Native.GA_ROOT);
            var owner = Native.GetWindow(root, Native.GW_OWNER);
            if (owner != _target && root != _target && !Desktop.PopupsOf(_target).Any(p => p.Handle == root)) _target = root;
        }
        var prev = Read();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2_500)
        {
            Thread.Sleep(200);
            var now = Read();
            if (now.Snap.Version == prev.Snap.Version) return now;
            prev = now;
        }
        return prev;
    }

    private Outcome Judge(string kind, Live before, Target? t, string what, Func<(bool ok, string? how)> perform, string? expect = null)
    {
        if (before.Snap.BlockedApp != null) throw new ToolError("blocked_app", $"{before.Snap.BlockedApp} is blocked by Reach privacy rules");
        // The target's own window is disabled under a dialog: the click would go nowhere (or to the dialog).
        if (t != null && t.Owner == before.Hwnd && before.Snap.PopupList.Count > 0 && !Native.IsWindowEnabled(before.Hwnd))
        {
            var p = before.Snap.PopupList[0];
            return new Outcome(Verdict.BlockedByPopup,
                $"verdict: blocked_by_popup · {what} is under \"{p.Title}\" ({p.WindowId}); nothing was done\n" +
                "answer the popup first (its elements are listed first):\n" + ViewText.Full(before.Snap), before.Snap);
        }
        var events = Uia.FocusEvents;
        var front = Native.GetForegroundWindow();
        var (ok, how) = perform();
        if (!ok) throw new ToolError("refused", $"couldn't {what}; observe and try another way (e.g. a point target)");
        var after = Settle(front);
        var diff = Diff.Of(before.Snap, after.Snap);
        var verdict = Verdicts.Judge(kind, diff, Uia.FocusEvents - events, t?.El?.Id, expect);
        var head = new StringBuilder($"verdict: {verdict.Word()} · {what}");
        if (how != null) head.Append(" (").Append(how).Append(')');
        if (t?.Note != null) head.Append(" (").Append(t.Note).Append(')');
        var body = diff.MostlyNew ? ViewText.Full(after.Snap) : $"{ViewText.Fence}\n{diff.Text()}";
        return new Outcome(verdict, $"{head}\n{body}", after.Snap);
    }

    private Outcome NeedsForeground(Live live, string what) =>
        new(Verdict.NeedsForeground,
            $"verdict: needs_foreground · {what} needs the real mouse/keyboard, which is off (foreground=false or --background-only); nothing was done",
            live.Snap);

    private bool ForegroundAllowed(JsonObject a) => !backgroundOnly && a.Bool("foreground") != false;

    /// <summary>Real input on [hwnd]: waits for the user to pause, brings the window to front, checks the point is on it.</summary>
    private void PrepareReal(IntPtr hwnd, int? x = null, int? y = null)
    {
        Input.WaitForUserPause();
        if (!Desktop.Focus(hwnd)) Console.Error.WriteLine("could not bring the window to front");
        if (x is int px && y is int py)
        {
            var hit = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = px, Y = py }), Native.GA_ROOT);
            var ok = hit == hwnd || Native.GetWindow(hit, Native.GW_OWNER) == hwnd || Native.GetAncestor(hit, Native.GA_ROOTOWNER) == Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
            if (!ok) throw new ToolError("blocked_by_popup", $"another window (\"{Native.Text(hit)}\") covers {px},{py}; move or close it first");
        }
    }

    /// <summary>
    /// UI Automation calls that open a modal dialog (Invoke on "Save As…") don't return until it closes. They get
    /// 1.5 s; after that the click has happened and the dialog is up, so Reach moves on.
    /// </summary>
    private static bool Patient(Action a)
    {
        Exception? err = null;
        var th = new Thread(() => { try { a(); } catch (Exception e) { err = e; } }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        if (!th.Join(1_500)) return true;
        if (err != null) Console.Error.WriteLine("pattern call failed: " + err.Message);
        return err == null;
    }

    private static T? Pattern<T>(IUIAutomationElement n, int id) where T : class
    {
        try { return n.GetCurrentPattern(id) as T; } catch { return null; }
    }

    private static bool HasBackground(IUIAutomationElement n)
    {
        if (Pattern<IUIAutomationInvokePattern>(n, UIA_InvokePatternId) != null) return true;
        if (Pattern<IUIAutomationTogglePattern>(n, UIA_TogglePatternId) != null) return true;
        if (Pattern<IUIAutomationSelectionItemPattern>(n, UIA_SelectionItemPatternId) != null) return true;
        if (Pattern<IUIAutomationExpandCollapsePattern>(n, UIA_ExpandCollapsePatternId) != null) return true;
        try { return !string.IsNullOrEmpty(Pattern<IUIAutomationLegacyIAccessiblePattern>(n, UIA_LegacyIAccessiblePatternId)?.CurrentDefaultAction); }
        catch { return false; }
    }

    /// <summary>Press without the mouse: invoke, toggle, select or expand, whichever the element offers.</summary>
    private static (bool ok, string? how)? Background(IUIAutomationElement n)
    {
        if (Pattern<IUIAutomationInvokePattern>(n, UIA_InvokePatternId) is { } inv) return (Patient(inv.Invoke), "invoked in the background");
        if (Pattern<IUIAutomationTogglePattern>(n, UIA_TogglePatternId) is { } tog) return (Patient(tog.Toggle), "toggled in the background");
        if (Pattern<IUIAutomationSelectionItemPattern>(n, UIA_SelectionItemPatternId) is { } sel) return (Patient(sel.Select), "selected in the background");
        if (Pattern<IUIAutomationExpandCollapsePattern>(n, UIA_ExpandCollapsePatternId) is { } ec)
        {
            var open = ec.CurrentExpandCollapseState == ExpandCollapseState.ExpandCollapseState_Expanded;
            return (Patient(open ? ec.Collapse : ec.Expand), open ? "collapsed in the background" : "expanded in the background");
        }
        if (Pattern<IUIAutomationLegacyIAccessiblePattern>(n, UIA_LegacyIAccessiblePatternId) is { } acc)
        {
            string? def = null;
            try { def = acc.CurrentDefaultAction; } catch { }
            if (!string.IsNullOrEmpty(def)) return (Patient(acc.DoDefaultAction), $"'{def}' in the background");
        }
        return null;
    }

    // ============================================================================================ act

    private Outcome Act(JsonObject a)
    {
        var action = a.Str("action") ?? throw new ToolError("bad_args", $"action is needed: {string.Join(", ", Catalog.Actions)}");
        var live = Read();
        var fgOk = ForegroundAllowed(a);
        switch (action)
        {
            case "tap":
            {
                var t = Resolve(live, a["target"]);
                var what = $"click {t.Describe()}";
                if (t.Node != null && t.El != null && HasBackground(t.Node))
                {
                    var bg = Judge("tap", live, t, what, () => Background(t.Node) ?? (false, null));
                    // A background press that changed nothing: one honest retry with the real mouse.
                    if (bg.Verdict != Verdict.NoEffect || !fgOk || bg.Text.StartsWith("verdict: blocked")) return bg;
                    var again = Read();
                    var t2 = Resolve(again, t.El.Id);
                    var real = Judge("tap", again, t2, what, () => { PrepareReal(again.Hwnd, t2.X, t2.Y); Input.Click(t2.X, t2.Y); return (true, "background press did nothing; used the mouse"); });
                    return real;
                }
                if (!fgOk) return NeedsForeground(live, what);
                return Judge("tap", live, t, what, () => { PrepareReal(t.Owner, t.X, t.Y); Input.Click(t.X, t.Y); return (true, "used the mouse"); });
            }
            case "long_press" or "right_click" or "double_tap" or "hover":
            {
                var t = Resolve(live, a["target"]);
                var verb = action switch { "double_tap" => "double-click", "hover" => "hover over", _ => "right-click" };
                var what = $"{verb} {t.Describe()}";
                if (!fgOk) return NeedsForeground(live, what);
                return Judge(action, live, t, what, () =>
                {
                    PrepareReal(t.Owner, t.X, t.Y);
                    if (action == "hover") Input.Hover(t.X, t.Y);
                    else Input.Click(t.X, t.Y, right: action != "double_tap", count: action == "double_tap" ? 2 : 1);
                    return (true, "used the mouse");
                });
            }
            case "type" or "clear":
                return Type(a, live, action, fgOk);
            case "scroll":
                return Scroll(a, live, fgOk);
            case "swipe" or "drag":
            {
                var from = Resolve(live, a["target"]);
                var to = a["to"] != null ? Resolve(live, a["to"]) : null;
                int x2, y2;
                if (to != null) { x2 = to.X; y2 = to.Y; }
                else
                {
                    var d = a.Str("direction") ?? throw new ToolError("bad_args", $"{action} needs 'to' or 'direction'");
                    (x2, y2) = d switch
                    {
                        "up" => (from.X, from.Y - 300), "down" => (from.X, from.Y + 300),
                        "left" => (from.X - 300, from.Y), "right" => (from.X + 300, from.Y),
                        _ => throw new ToolError("bad_args", "direction must be up, down, left or right"),
                    };
                }
                var what = $"drag from {from.X},{from.Y} to {x2},{y2}";
                if (!fgOk) return NeedsForeground(live, what);
                var ms = Math.Clamp(a.Int("duration_ms") ?? 400, 100, 5_000);
                return Judge("drag", live, from, what, () => { PrepareReal(from.Owner, from.X, from.Y); Input.Drag(from.X, from.Y, x2, y2, ms); return (true, "used the mouse"); });
            }
            case "key":
            {
                var k = a.Str("key") ?? throw new ToolError("bad_args", "key needs key, e.g. enter, ctrl+s");
                var what = $"press {k}";
                if (!fgOk) return NeedsForeground(live, what);
                var into = live.Snap.PopupList.Count > 0 ? Desktop.Parse(live.Snap.PopupList[0].WindowId) ?? live.Hwnd : live.Hwnd;
                return Judge("key", live, null, what, () => { PrepareReal(into); Input.Combo(k); return (true, "keyboard"); });
            }
            default:
                throw new ToolError("bad_args", $"unknown action '{action}'; actions: {string.Join(", ", Catalog.Actions)}");
        }
    }

    private Outcome Type(JsonObject a, Live live, string action, bool fgOk)
    {
        var text = action == "clear" ? "" : a.Str("text") ?? throw new ToolError("bad_args", "type needs text");
        var append = a.Str("mode") == "append";
        var submit = a.Bool("submit") == true;
        Target? t = a["target"] != null ? Resolve(live, a["target"]) : null;
        if (t == null)
        {
            var f = live.Snap.Elements.FirstOrDefault(e => e.Raw.Focused && e.Role == Role.Field)
                    ?? live.Snap.Elements.FirstOrDefault(e => e.Role == Role.Field);
            if (f == null) throw new ToolError("not_found", "no text box in the window; give a target");
            t = Of(live, f, f.Raw.Focused ? null : $"no box had focus, used {f.Id}");
        }
        if (t.El?.Role == Role.Password) throw new ToolError("blocked", "Reach does not type into password fields; ask the user to enter it");
        var node = t.Node ?? throw new ToolError("not_found", "no element at that point to type into");
        var want = append ? (t.El?.Value ?? "") + text : text;
        var what = (action == "clear" ? "clear " : $"type \"{ViewText.Clip(text, 40)}\" into ") + t.Describe() + (submit ? " + Enter" : "");
        var expect = t.El?.Role == Role.Field ? want : null;

        var value = Pattern<IUIAutomationValuePattern>(node, UIA_ValuePatternId);
        var canSet = value != null && SafeReadOnly(value) == false;
        if (canSet && (!submit || fgOk))
        {
            return Judge("type", live, t, what, () =>
            {
                value!.SetValue(want);
                if (submit) { PrepareReal(t.Owner); try { node.SetFocus(); } catch { } Input.Press(0x0D); }
                return (true, submit ? "set in the background, Enter with the keyboard" : "set in the background");
            }, submit ? null : expect);
        }
        if (!fgOk) return NeedsForeground(live, what);
        return Judge("type", live, t, what, () =>
        {
            PrepareReal(t.Owner);
            try { node.SetFocus(); } catch { Input.Click(t.X, t.Y); }
            Thread.Sleep(60);
            if (!append) { Input.Combo("ctrl+a"); if (text.Length == 0) Input.Press(0x2E); }
            Input.Type(text);
            if (submit) Input.Press(0x0D);
            return (true, "typed with the keyboard");
        }, submit ? null : expect);
    }

    private static bool? SafeReadOnly(IUIAutomationValuePattern v)
    {
        try { return v.CurrentIsReadOnly != 0; } catch { return null; }
    }

    private Outcome Scroll(JsonObject a, Live live, bool fgOk)
    {
        var dir = a.Str("direction") ?? "down";
        Target? t = a["target"] != null ? Resolve(live, a["target"]) : null;
        var list = t?.El is { Raw.Scrollable: true } ? t
            : live.Snap.Elements.Where(e => e.Raw.Scrollable).MaxBy(e => e.Bounds.Area) is { } big ? Of(live, big, null) : t;
        var what = $"scroll {dir}" + (list?.El != null ? $" in {list.Describe()}" : "");
        var (h, v) = dir switch
        {
            "down" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeIncrement),
            "up" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeDecrement),
            "right" => (ScrollAmount.ScrollAmount_LargeIncrement, ScrollAmount.ScrollAmount_NoAmount),
            "left" => (ScrollAmount.ScrollAmount_LargeDecrement, ScrollAmount.ScrollAmount_NoAmount),
            _ => throw new ToolError("bad_args", "direction must be up, down, left or right"),
        };
        var sp = list?.Node != null ? Pattern<IUIAutomationScrollPattern>(list.Node, UIA_ScrollPatternId) : null;
        if (sp != null)
        {
            return Judge("scroll", live, null, what, () =>
            {
                try { sp.Scroll(h, v); return (true, "scrolled in the background"); }
                catch { return (false, null); }
            });
        }
        if (!fgOk) return NeedsForeground(live, what);
        var x = list?.X ?? live.Snap.Window.Cx;
        var y = list?.Y ?? live.Snap.Window.Cy;
        var notches = dir is "down" or "right" ? -5 : 5;
        if (dir is "right" or "left") notches = -notches;
        return Judge("scroll", live, null, what, () =>
        {
            Input.WaitForUserPause();
            Input.Wheel(x, y, notches, dir is "left" or "right");
            return (true, "mouse wheel");
        });
    }

    // ============================================================================================ act_batch

    private ToolResult ActBatch(JsonObject a)
    {
        var steps = a["actions"] as JsonArray ?? throw new ToolError("bad_args", "actions list is needed");
        if (steps.Count == 0) throw new ToolError("bad_args", "actions list is empty");
        if (steps.Count > 20) throw new ToolError("bad_args", "at most 20 steps per batch");
        var first = Read().Snap;
        var lines = new List<string>();
        Snapshot? last = null;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject step) throw new ToolError("bad_args", "each action must be an object");
            Outcome o;
            try { o = Act(step); }
            catch (ToolError e)
            {
                lines.Add($"step {i + 1}: error {e.Code} · {e.Hint}");
                if (i + 1 < steps.Count) lines.Add($"stopped; steps {i + 2}–{steps.Count} not run");
                break;
            }
            last = o.After;
            lines.Add($"step {i + 1}: " + o.Text.Split('\n')[0].Replace("verdict: ", ""));
            if (o.Verdict != Verdict.Done)
            {
                if (i + 1 < steps.Count) lines.Add($"stopped; steps {i + 2}–{steps.Count} not run");
                break;
            }
        }
        lines.Add(Render(last ?? Read().Snap, first.Version));
        return ToolResult.Text(string.Join('\n', lines));
    }

    // ============================================================================================ wait_for

    private ToolResult WaitFor(JsonObject a)
    {
        var timeout = Math.Clamp(a.Int("timeout_ms") ?? 5_000, 100, 30_000);
        var text = a.Str("text") is string tx ? Ids.Normalize(tx) : null;
        var gone = a.Str("gone") is string g ? Ids.Normalize(g) : null;
        var id = a.Str("id");
        var window = a.Str("window");
        var idle = a.Bool("idle") == true;
        if (text == null && gone == null && id == null && window == null && !idle) throw new ToolError("bad_args", "give one of text, gone, id, window, idle");
        var sw = Stopwatch.StartNew();
        var before = _state.Latest()?.Version;
        int? lastSig = null;
        var stableSince = 0L;
        while (true)
        {
            if (window != null)
            {
                var w = Desktop.Match(window);
                if (w == null)
                {
                    if (sw.ElapsedMilliseconds > timeout) return ToolResult.Text($"verdict: timeout · no window like \"{window}\" after {timeout / 1000.0} s");
                    Thread.Sleep(250);
                    continue;
                }
                _target = w.Handle;
            }
            var live = Read();
            var els = live.Snap.Elements;
            var sig = live.Snap.Signature;
            if (sig != lastSig) { lastSig = sig; stableSince = sw.ElapsedMilliseconds; }
            var met = (text == null || els.Any(e => Ids.Normalize(e.Label).Contains(text) || Ids.Normalize(e.Value ?? "").Contains(text)))
                      && (gone == null || !els.Any(e => Ids.Normalize(e.Label).Contains(gone)))
                      && (id == null || els.Any(e => e.Id == id))
                      && (!idle || sw.ElapsedMilliseconds - stableSince >= 800);
            if (met) return ToolResult.Text($"met after {sw.ElapsedMilliseconds / 100 / 10.0} s\n" + Render(live.Snap, before));
            if (sw.ElapsedMilliseconds > timeout) return ToolResult.Text($"verdict: timeout · not met after {timeout / 1000.0} s\n" + Render(live.Snap, before));
            // Focus moving or a window opening wakes the wait early; otherwise look again every 300 ms.
            var ev = Uia.FocusEvents;
            for (var i = 0; i < 6 && ev == Uia.FocusEvents; i++) Thread.Sleep(50);
        }
    }

    // ============================================================================================ find

    private ToolResult Find(JsonObject a)
    {
        var text = a.Str("text") is string tx ? Ids.Normalize(tx) : null;
        var role = a.Str("role")?.ToLowerInvariant();
        var rid = a.Str("resource_id");
        var limit = Math.Clamp(a.Int("limit") ?? 20, 1, 100);
        if (text == null && role == null && rid == null) throw new ToolError("bad_args", "give text, role or resource_id");
        bool Match(Element e) =>
            (text == null || Ids.Normalize(e.Label).Contains(text) || Ids.Normalize(e.Value ?? "").Contains(text)) &&
            (role == null || e.Role.Word() == role) && (rid == null || e.Raw.ResourceId == rid);

        var live = Read();
        var scrolls = 0;
        if (a.Bool("scroll") == true)
        {
            while (!live.Snap.Elements.Any(Match) && scrolls < 10 && live.Snap.BlockedApp == null)
            {
                var list = live.Snap.Elements.Where(e => e.Raw.Scrollable).MaxBy(e => e.Bounds.Area);
                if (list == null) break;
                if (!live.Nodes.TryGetValue(list.Id, out var listNode)) break;
                var sp = Pattern<IUIAutomationScrollPattern>(listNode, UIA_ScrollPatternId);
                if (sp == null) break;
                try { sp.Scroll(ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeIncrement); } catch { break; }
                scrolls++;
                var v = live.Snap.Version;
                Thread.Sleep(250);
                live = Read();
                if (live.Snap.Version == v) break; // end of the list
            }
        }
        var hits = live.Snap.Elements.Where(Match).ToList();
        var head = ViewText.Header(live.Snap) + (scrolls > 0 ? $" · scrolled {scrolls}×" : "");
        if (hits.Count == 0) return ToolResult.Text($"{head}\nno match" + (a.Bool("scroll") == true ? "" : " (scroll=true looks further down)"));
        return ToolResult.Text($"{head}\n{ViewText.Fence}\n" + string.Join('\n', hits.Take(limit).Select(e => ViewText.Line(e, true))) +
                               (hits.Count > limit ? $"\n… {hits.Count - limit} more" : ""));
    }

    // ============================================================================================ apps

    private ToolResult Apps(JsonObject a)
    {
        var q = a.Str("query")?.ToLowerInvariant();
        switch (a.Str("action"))
        {
            case "list" when a.Bool("installed") == true:
            {
                var apps = StartApps.All().Where(x => q == null || x.Name.ToLowerInvariant().Contains(q)).ToList();
                return ToolResult.Text($"{apps.Count} installed apps\n" + string.Join('\n', apps.Select(x => x.Name)));
            }
            case "list":
            {
                var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
                var wins = Desktop.Windows().Where(w => q == null || w.Title.ToLowerInvariant().Contains(q) || w.App.Contains(q)).ToList();
                return ToolResult.Text($"{wins.Count} open windows\n" + string.Join('\n', wins.Select(w =>
                    $"{w.Id} {w.App} \"{ViewText.Clip(w.Title, 70)}\"{(w.Minimized ? " minimized" : "")}{(w.Handle == fg ? " (front)" : "")}" +
                    $"{(w.Handle == _target ? " (working)" : "")}{(Privacy.IsBlocked(w.App, w.Title) ? " (blocked by Reach privacy rules)" : "")}")));
            }
            case "open":
            {
                var name = a.Str("name") ?? throw new ToolError("bad_args", "open needs name");
                if (Privacy.IsBlocked(name, name)) throw new ToolError("blocked_app", $"{name} is blocked by Reach privacy rules");
                var before = Desktop.Windows().Select(w => w.Handle).ToHashSet();
                var front = Native.GetForegroundWindow();
                var launched = StartApps.Launch(name);
                var sw = Stopwatch.StartNew();
                IntPtr found = IntPtr.Zero;
                while (sw.ElapsedMilliseconds < 10_000 && found == IntPtr.Zero)
                {
                    Thread.Sleep(250);
                    var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
                    if (fg != IntPtr.Zero && fg != front && !Privacy.IsBlocked(Desktop.AppOf(fg), Native.Text(fg))) found = fg;
                    else found = Desktop.Windows().FirstOrDefault(w => !before.Contains(w.Handle))?.Handle ?? IntPtr.Zero;
                }
                if (found == IntPtr.Zero) return ToolResult.Text($"verdict: unknown · started {launched}; no new window after 10 s (apps list shows what's open)");
                _target = found;
                Thread.Sleep(400); // let its first screen draw
                var live = Read();
                return ToolResult.Text($"verdict: done · opened {launched} (now the working window)\n" + ViewText.Full(live.Snap));
            }
            case "switch":
            {
                var name = a.Str("name") ?? throw new ToolError("bad_args", "switch needs name: a window id or words from its title");
                var w = Desktop.Match(name) ?? throw new ToolError("not_found", $"no open window matches \"{name}\"; apps list shows them");
                _target = w.Handle;
                var brought = !backgroundOnly && Desktop.Focus(w.Handle);
                var live = Read();
                return ToolResult.Text($"verdict: done · working in {w.Id} \"{w.Title}\"{(brought ? " (brought to front)" : " (left where it is)")}\n" + ViewText.Full(live.Snap));
            }
            case "current":
            {
                var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
                var t = Working();
                return ToolResult.Text($"front {Desktop.IdOf(fg)} {Desktop.AppOf(fg)} \"{Native.Text(fg)}\"\n" +
                                       $"working {Desktop.IdOf(t)} {Desktop.AppOf(t)} \"{Native.Text(t)}\"");
            }
            case "close":
            {
                var w = a.Str("name") is string n ? Desktop.Match(n) ?? throw new ToolError("not_found", $"no open window matches \"{n}\"") : Desktop.Info(Working());
                Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2_000 && Native.IsWindow(w.Handle) && Desktop.PopupsOf(w.Handle).Count == 0) Thread.Sleep(100);
                if (!Native.IsWindow(w.Handle) || !Native.IsWindowVisible(w.Handle))
                {
                    if (_target == w.Handle) _target = IntPtr.Zero;
                    return ToolResult.Text($"verdict: done · closed {w.Id} \"{w.Title}\"");
                }
                _target = w.Handle;
                var live = Read();
                return ToolResult.Text($"verdict: unknown · asked {w.Id} to close; it is still open" +
                                       (live.Snap.PopupList.Count > 0 ? " and asks something (answer the popup)" : "") + "\n" + ViewText.Full(live.Snap));
            }
            default:
                throw new ToolError("bad_args", "action must be list, open, switch, current or close");
        }
    }

    // ============================================================================================ device

    private ToolResult Device()
    {
        Native.GetSystemPowerStatus(out var p);
        var battery = p.BatteryFlag == 128 ? "no battery" : $"battery {p.BatteryLifePercent}%{(p.ACLineStatus == 1 ? " plugged in" : "")}";
        var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
        var idle = Input.IdleMs();
        var lines = new List<string>
        {
            $"pc {Environment.MachineName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
        };
        lines.AddRange(Desktop.Monitors());
        lines.Add($"front window {Desktop.IdOf(fg)} {Desktop.AppOf(fg)} \"{ViewText.Clip(Native.Text(fg), 60)}\"");
        if (_target != IntPtr.Zero && Native.IsWindow(_target)) lines.Add($"working window {Desktop.IdOf(_target)} \"{ViewText.Clip(Native.Text(_target), 60)}\"");
        lines.Add(idle < 2_000 ? "user: using the mouse/keyboard right now" : $"user: idle for {idle / 1000} s");
        lines.Add(battery);
        lines.Add($"reach {Program.Version} · {(backgroundOnly ? "background only (never the real mouse/keyboard)" : "background first, real mouse/keyboard when needed")} · worker pid {Environment.ProcessId}");
        return ToolResult.Text(string.Join('\n', lines));
    }
}
