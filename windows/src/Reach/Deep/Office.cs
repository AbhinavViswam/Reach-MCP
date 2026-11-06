using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Deep;

/// <summary>Running Office apps through COM. Busy apps ("call rejected") are retried for a few seconds.</summary>
internal static class Com
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    /// <summary>The app already open, else null.</summary>
    public static dynamic? Running(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var o) == 0 ? o : null;
    }

    public static dynamic Start(string progId, string name)
    {
        var t = Type.GetTypeFromProgID(progId) ?? throw new ToolError("not_supported", $"{name} isn't installed (classic desktop {name} is needed)");
        dynamic app = Activator.CreateInstance(t)!;
        try { app.Visible = true; } catch { }
        return app;
    }

    /// <summary>Runs [f], retrying while the app is busy (a dialog open, a cell being edited).</summary>
    public static T Call<T>(Func<T> f, string app)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return f(); }
            catch (COMException e) when ((uint)e.HResult is 0x80010001 or 0x8001010A && DateTime.UtcNow < until) { Thread.Sleep(200); }
            catch (COMException e) when ((uint)e.HResult is 0x80010001 or 0x8001010A)
            {
                throw new ToolError("busy", $"{app} is busy (a dialog is open or a cell is being edited); finish that first");
            }
            catch (COMException e) { throw new ToolError("office_error", $"{app} said: {e.Message}"); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e) { throw new ToolError("office_error", $"{app} doesn't support that: {e.Message}"); }
        }
    }
}

/// <summary>Word, Excel and Outlook (classic) without clicking through their windows.</summary>
internal sealed class OfficeTool
{
    public ToolResult Run(JsonObject a)
    {
        var app = a.Str("app")?.ToLowerInvariant() ?? throw new ToolError("bad_args", "app is needed: excel, word or outlook");
        var action = a.Str("action") ?? throw new ToolError("bad_args", "action is needed");
        // App rules count here too: a read-only or blocked Office app can't be changed through COM either.
        var proc = app switch { "excel" => "excel", "word" => "winword", "outlook" => "outlook", _ => app };
        if (Privacy.Rule(proc) == AppRule.Block) throw new ToolError("blocked_app", $"{app} is blocked by Reach privacy rules");
        if (action is not ("list" or "read" or "sheets" or "inbox")) Privacy.RequireCanAct(proc, null, app);
        return app switch
        {
            "excel" => Com.Call(() => Excel(action, a), "Excel"),
            "word" => Com.Call(() => Word(action, a), "Word"),
            "outlook" => Com.Call(() => Outlook(action, a), "Outlook"),
            _ => throw new ToolError("bad_args", "app must be excel, word or outlook"),
        };
    }

    private static string Fenced(string text)
    {
        var (t, masked) = Mask.Apply(text);
        return ViewText.Fence + "\n" + t + (masked ? "\n" + Core.Mask.Note : "");
    }

    // ============================================================================================ Excel

    /// <summary>A range's Formula (one value, or a 1-based 2-D array) as rows of strings, for undo.</summary>
    private static JsonArray Grid(object? f)
    {
        var rows = new JsonArray();
        if (f is object[,] g)
        {
            for (var i = g.GetLowerBound(0); i <= g.GetUpperBound(0); i++)
            {
                var row = new JsonArray();
                for (var j = g.GetLowerBound(1); j <= g.GetUpperBound(1); j++) row.Add(g[i, j]?.ToString() ?? "");
                rows.Add(row);
            }
        }
        else rows.Add(new JsonArray(f?.ToString() ?? ""));
        return rows;
    }

    /// <summary>Undo: puts [cells] back into [address] of an open workbook.</summary>
    public static void PutCells(string bookPath, string sheetName, string address, JsonArray cells) => Com.Call<int>(() =>
    {
        dynamic xl = Com.Running("Excel.Application") ?? throw new ToolError("not_found", "Excel isn't open; open the workbook first");
        dynamic? book = null;
        foreach (dynamic b in xl.Workbooks) if (string.Equals((string)b.FullName, bookPath, StringComparison.OrdinalIgnoreCase)) book = b;
        if (book == null) throw new ToolError("not_found", $"{Path.GetFileName(bookPath)} isn't open; open it first");
        dynamic range = book.Worksheets[sheetName].Range[address];
        var h = cells.Count;
        var w = cells.Max(r => (r as JsonArray)?.Count ?? 1);
        var grid = new object?[h, w];
        for (var i = 0; i < h; i++)
            for (var j = 0; j < w; j++)
                grid[i, j] = cells[i] is JsonArray r && j < r.Count ? r[j]?.ToString() : null;
        range.Formula = grid;
        return 0;
    }, "Excel");

    private ToolResult Excel(string action, JsonObject a)
    {
        dynamic? xl = Com.Running("Excel.Application");
        if (action is "open" or "new")
        {
            xl ??= Com.Start("Excel.Application", "Excel");
            dynamic wb = action == "open" ? xl.Workbooks.Open(a.Str("path") ?? throw new ToolError("bad_args", "open needs path")) : xl.Workbooks.Add();
            return ToolResult.Text($"verdict: done · {(action == "open" ? "opened" : "new workbook")} {wb.Name} · sheets: {Sheets(wb)}");
        }
        if (xl == null) throw new ToolError("not_found", "Excel isn't open; use action open (with path) or new");
        if (action == "list")
        {
            var sb = new StringBuilder();
            foreach (dynamic wb in xl.Workbooks) sb.Append($"{wb.Name} · {wb.FullName} · sheets: {Sheets(wb)}{(wb.Saved ? "" : " · unsaved changes")}\n");
            return ToolResult.Text(sb.Length == 0 ? "no workbooks open" : sb.ToString().TrimEnd());
        }
        dynamic book = a.Str("workbook") is string n ? xl.Workbooks[n] : xl.ActiveWorkbook ?? throw new ToolError("not_found", "no workbook open");
        dynamic sheet = a.Str("sheet") is string s ? book.Worksheets[s] : book.ActiveSheet;
        switch (action)
        {
            case "read":
            {
                string addr = a.Str("range") ?? (string)sheet.UsedRange.Address(false, false);
                dynamic r = sheet.Range[addr];
                if ((int)r.Cells.Count > 5000) throw new ToolError("too_big", $"{addr} has {r.Cells.Count} cells; read up to 5000 at a time");
                var formulas = a.Bool("formulas") == true;
                object v = formulas ? r.Formula : r.Value2;
                var text = new StringBuilder();
                if (v is object[,] grid)
                {
                    for (var i = grid.GetLowerBound(0); i <= grid.GetUpperBound(0); i++)
                    {
                        var row = new List<string>();
                        for (var j = grid.GetLowerBound(1); j <= grid.GetUpperBound(1); j++) row.Add(Cell(grid[i, j]));
                        text.Append(string.Join('\t', row)).Append('\n');
                    }
                }
                else text.Append(Cell(v));
                return ToolResult.Text($"{book.Name} › {sheet.Name} › {addr} (tab-separated rows{(formulas ? ", formulas" : "")})\n" + Fenced(text.ToString().TrimEnd('\n')));
            }
            case "write":
            {
                var addr = a.Str("range") ?? throw new ToolError("bad_args", "write needs range, e.g. A1 or A1:C3");
                var rows = a["values"] as JsonArray ?? throw new ToolError("bad_args", "write needs values: [[\"a\", 1], [\"=SUM(B1:B2)\"]]");
                var h = rows.Count;
                var w = rows.Max(r => r is JsonArray ra ? ra.Count : 1);
                var grid = new object?[h, w];
                for (var i = 0; i < h; i++)
                {
                    var row = rows[i] as JsonArray ?? new JsonArray(rows[i]?.DeepClone());
                    for (var j = 0; j < w; j++) grid[i, j] = j < row.Count ? Value(row[j]) : null;
                }
                dynamic start = sheet.Range[addr].Cells[1, 1];
                dynamic target = sheet.Range[start, start.Offset[h - 1, w - 1]];
                Undo.CellsBefore((string)book.FullName, (string)sheet.Name, (string)target.Address(false, false), Grid(target.Formula));
                // Formula takes plain values too, so "=SUM(…)" cells become formulas and the rest stay values.
                target.Formula = grid;
                return ToolResult.Text($"verdict: done · wrote {h}×{w} cells at {target.Address(false, false)} in {sheet.Name} (not saved yet)");
            }
            case "save":
            {
                if (a.Str("path") is string p) book.SaveAs(p); else book.Save();
                return ToolResult.Text($"verdict: done · saved {book.FullName}");
            }
            case "sheets":
                return ToolResult.Text($"{book.Name} sheets: {Sheets(book)} · active {sheet.Name} · used {sheet.UsedRange.Address(false, false)}");
            default:
                throw new ToolError("bad_args", "excel actions: list, open, new, read, write, save, sheets");
        }
    }

    private static string Sheets(dynamic wb)
    {
        var names = new List<string>();
        foreach (dynamic s in wb.Worksheets) names.Add((string)s.Name);
        return string.Join(", ", names);
    }

    private static string Cell(object? v) => v switch
    {
        null => "",
        double d => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString() : d.ToString("G15"),
        int i => i == -2146826281 ? "#DIV/0!" : i.ToString(), // Excel error codes arrive as ints
        _ => v.ToString()!.Replace('\t', ' ').Replace('\n', ' '),
    };

    private static object? Value(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    // ============================================================================================ Word

    private const int WdStyleNormal = -1, WdStyleHeading1 = -2, WdStyleHeading2 = -3, WdStyleHeading3 = -4, WdStyleListBullet = -49, WdStyleListNumber = -50;

    private ToolResult Word(string action, JsonObject a)
    {
        dynamic? wd = Com.Running("Word.Application");
        if (action is "open" or "new")
        {
            wd ??= Com.Start("Word.Application", "Word");
            dynamic doc = action == "open" ? wd.Documents.Open(a.Str("path") ?? throw new ToolError("bad_args", "open needs path")) : wd.Documents.Add();
            return ToolResult.Text($"verdict: done · {(action == "open" ? "opened" : "new document")} {doc.Name}");
        }
        if (wd == null) throw new ToolError("not_found", "Word isn't open; use action open (with path) or new");
        if (action == "list")
        {
            var sb = new StringBuilder();
            foreach (dynamic d in wd.Documents) sb.Append($"{d.Name} · {d.FullName} · {d.Paragraphs.Count} paragraphs{(d.Saved ? "" : " · unsaved changes")}\n");
            return ToolResult.Text(sb.Length == 0 ? "no documents open" : sb.ToString().TrimEnd());
        }
        dynamic docu = a.Str("document") is string n ? wd.Documents[n] : wd.ActiveDocument;
        switch (action)
        {
            case "read":
            {
                string text = docu.Content.Text;
                var max = 20_000;
                return ToolResult.Text($"{docu.Name} · {docu.Paragraphs.Count} paragraphs{(text.Length > max ? " (first 20000 characters)" : "")}\n" +
                                       Fenced(text.Length > max ? text[..max] : text.Replace('\r', '\n')));
            }
            case "append":
            {
                var text = a.Str("text") ?? throw new ToolError("bad_args", "append needs text");
                var style = (a.Str("style") ?? "normal").ToLowerInvariant() switch
                {
                    "heading1" or "heading" or "title" => WdStyleHeading1, "heading2" => WdStyleHeading2, "heading3" => WdStyleHeading3,
                    "bullet" or "bullets" => WdStyleListBullet, "numbered" => WdStyleListNumber, _ => WdStyleNormal,
                };
                // Each line becomes its own paragraph in the chosen style (localised style names don't matter: built-in ids).
                foreach (var line in text.Replace("\r", "").Split('\n'))
                {
                    dynamic end = docu.Content;
                    var empty = ((string)end.Text).Trim().Length == 0;
                    if (!empty) end.InsertParagraphAfter();
                    dynamic p = docu.Paragraphs[docu.Paragraphs.Count];
                    p.Range.Text = line;
                    p = docu.Paragraphs[docu.Paragraphs.Count];
                    p.Style = style;
                }
                return ToolResult.Text($"verdict: done · added {text.Split('\n').Length} paragraph(s) to {docu.Name} (not saved yet)");
            }
            case "replace":
            {
                var find = a.Str("find") ?? throw new ToolError("bad_args", "replace needs find and text");
                var with = a.Str("text") ?? "";
                dynamic range = docu.Content;
                bool any = range.Find.Execute(FindText: find, MatchCase: false, ReplaceWith: with, Replace: 2 /* wdReplaceAll */);
                return ToolResult.Text(any ? $"verdict: done · replaced \"{find}\" with \"{with}\"" : $"verdict: no_effect · \"{find}\" not found");
            }
            case "save":
            {
                if (a.Str("path") is string p) docu.SaveAs2(p); else docu.Save();
                return ToolResult.Text($"verdict: done · saved {docu.FullName}");
            }
            default:
                throw new ToolError("bad_args", "word actions: list, open, new, read, append, replace, save");
        }
    }

    // ============================================================================================ Outlook

    private ToolResult Outlook(string action, JsonObject a)
    {
        dynamic ol = Com.Running("Outlook.Application") ?? Com.Start("Outlook.Application", "Outlook");
        dynamic ns = ol.GetNamespace("MAPI");
        switch (action)
        {
            case "inbox":
            {
                dynamic items = ns.GetDefaultFolder(6 /* olFolderInbox */).Items;
                items.Sort("[ReceivedTime]", true);
                var lim = Math.Clamp(a.Int("limit") ?? 15, 1, 50);
                var unreadOnly = a.Bool("unread") == true;
                var rows = new List<string>();
                foreach (dynamic m in items)
                {
                    if (rows.Count >= lim) break;
                    if (unreadOnly && !(bool)m.UnRead) continue;
                    rows.Add($"{m.EntryID[^12..]} · {(m.UnRead ? "unread · " : "")}{m.SenderName} · {((DateTime)m.ReceivedTime):ddd d MMM HH:mm} · {m.Subject}");
                }
                return ToolResult.Text(rows.Count == 0 ? "inbox empty" : $"{rows.Count} mails, newest first (id = last 12 characters)\n" + Fenced(string.Join('\n', rows)));
            }
            case "read":
            {
                var id = a.Str("id") ?? throw new ToolError("bad_args", "read needs id from inbox");
                dynamic items = ns.GetDefaultFolder(6).Items;
                items.Sort("[ReceivedTime]", true);
                foreach (dynamic m in items)
                {
                    if (!((string)m.EntryID).EndsWith(id)) continue;
                    string body = m.Body ?? "";
                    return ToolResult.Text($"from {m.SenderName} · {m.Subject} · {((DateTime)m.ReceivedTime):ddd d MMM HH:mm}\n" + Fenced(body.Length > 10_000 ? body[..10_000] : body));
                }
                throw new ToolError("not_found", $"no mail {id} in the inbox");
            }
            case "draft" or "send":
            {
                var to = a.Str("to") ?? throw new ToolError("bad_args", $"{action} needs to, subject, text");
                var subject = a.Str("subject") ?? "";
                var text = a.Str("text") ?? "";
                if (action == "send") Approve.Require($"send an email to {to}", $"Subject: {subject}\n\n{ViewText.Clip(text, 600)}");
                dynamic mail = ol.CreateItem(0 /* olMailItem */);
                mail.To = to;
                mail.Subject = subject;
                mail.Body = text;
                if (action == "send") { mail.Send(); Undo.CantUndo($"sent mail to {to}"); return ToolResult.Text($"verdict: done · sent to {to}"); }
                mail.Save();
                mail.Display(false);
                return ToolResult.Text($"verdict: done · draft to {to} saved and opened; the user can check it and press Send");
            }
            default:
                throw new ToolError("bad_args", "outlook actions: inbox, read, draft, send");
        }
    }
}
