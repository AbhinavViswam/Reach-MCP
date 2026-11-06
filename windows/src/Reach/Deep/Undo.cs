using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;
using Reach.Worker;

namespace Reach.Deep;

/// <summary>
/// The undo tool (spec/CONTRACT.md §6.2): list what Reach changed, undo the last change, or everything back to a
/// change or checkpoint. Files are put back from Reach's own backups (deleted files too), windows go back to where
/// they were, the clipboard gets its old text, Excel cells their old values. A file changed by someone else since
/// asks the user first.
/// </summary>
internal static class Undo
{
    // ============================================================================================ recording

    public static void FileWritten(string path, bool existed, string? backup)
    {
        if (!existed) Journal.Add("file_new", $"made {path}", new JsonObject { ["path"] = path, ["hash"] = Journal.Hash(path) });
        else Journal.Add("file_change", $"changed {path}", new JsonObject { ["path"] = path, ["backup"] = backup, ["hash"] = Journal.Hash(path) },
            undoable: backup != null);
    }

    public static void FileCopied(string to) =>
        Journal.Add("file_new", $"copied to {to}", new JsonObject { ["path"] = to, ["hash"] = Journal.Hash(to) });

    public static void FileMoved(string from, string to) =>
        Journal.Add("file_move", $"moved {from} → {to}", new JsonObject { ["from"] = from, ["to"] = to });

    public static void FileDeleted(string path, string? backup, bool folder) =>
        Journal.Add("file_delete", $"deleted {path}" + (folder || backup == null ? " (restore it from the Recycle Bin)" : ""),
            new JsonObject { ["path"] = path, ["backup"] = backup }, undoable: !folder && backup != null);

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Call before moving or resizing [hwnd]: remembers where it is now.</summary>
    public static void WindowBefore(IntPtr hwnd, string what)
    {
        var b = Desktop.Bounds(hwnd);
        var state = Native.IsIconic(hwnd) ? "min" : IsZoomed(hwnd) ? "max" : "normal";
        var top = (GetWindowLong(hwnd, -20) & 0x8) != 0;
        Journal.Add("window", what, new JsonObject
        {
            ["hwnd"] = hwnd.ToInt64(), ["title"] = Native.Text(hwnd), ["l"] = b.Left, ["t"] = b.Top, ["r"] = b.Right, ["b"] = b.Bottom,
            ["state"] = state, ["topmost"] = top,
        });
    }

    public static void ClipboardBefore(string? previous) =>
        Journal.Add("clipboard", "set the clipboard", new JsonObject { ["previous"] = previous });

    public static void CellsBefore(string book, string sheet, string address, JsonArray old) =>
        Journal.Add("excel_write", $"wrote {address} in {Path.GetFileName(book)} › {sheet}",
            new JsonObject { ["book"] = book, ["sheet"] = sheet, ["address"] = address, ["cells"] = old });

    /// <summary>Changes that can't be taken back still go in the list, marked so.</summary>
    public static void CantUndo(string what) => Journal.Add("info", what, new JsonObject(), undoable: false);

    // ============================================================================================ the tool

    public static ToolResult Run(JsonObject a)
    {
        var action = a.Str("action") ?? "list";
        switch (action)
        {
            case "list":
            {
                var all = Journal.All().Take(Math.Clamp(a.Int("limit") ?? 15, 1, 100)).ToList();
                if (all.Count == 0) return ToolResult.Text("nothing in the undo list yet");
                return ToolResult.Text("newest first:\n" + string.Join('\n', all.Select(Line)));
            }
            case "checkpoint":
            {
                var name = a.Str("name") ?? DateTime.Now.ToString("HH:mm:ss");
                var id = Journal.Add("checkpoint", $"checkpoint \"{name}\"", new JsonObject { ["name"] = name }, undoable: false);
                return ToolResult.Text($"verdict: done · checkpoint u{id} \"{name}\"; undo action=to target=\"{name}\" puts back everything after it");
            }
            case "last":
            {
                Pause.ThrowIfOn();
                var e = Journal.All().FirstOrDefault(x => x.Undoable && !x.Undone && x.Kind != "checkpoint")
                        ?? throw new ToolError("nothing", "nothing left to undo");
                return ToolResult.Text($"verdict: done · undid u{e.Id}: {UndoOne(e)}");
            }
            case "to":
            {
                Pause.ThrowIfOn();
                var target = a.Str("target") ?? a["target"]?.ToString() ?? throw new ToolError("bad_args", "to needs target: a change id (u7) or a checkpoint name");
                var all = Journal.All();
                int from;
                if (int.TryParse(target.TrimStart('u', 'U'), out var n)) from = n;
                else
                {
                    var cp = all.FirstOrDefault(x => x.Kind == "checkpoint" && x.Data.Str("name") == target)
                             ?? throw new ToolError("not_found", $"no checkpoint \"{target}\"");
                    from = cp.Id + 1;
                }
                var todo = all.Where(x => x.Id >= from && x.Undoable && !x.Undone && x.Kind != "checkpoint").ToList(); // newest first
                if (todo.Count == 0) return ToolResult.Text($"nothing to undo after u{from - 1}");
                var lines = new List<string>();
                foreach (var e in todo)
                {
                    try { lines.Add($"u{e.Id} undone: {UndoOne(e)}"); }
                    catch (ToolError err)
                    {
                        lines.Add($"u{e.Id} not undone: {err.Code} · {err.Hint}");
                        lines.Add($"stopped; {todo.Count - lines.Count + 1} older changes left as they are");
                        return ToolResult.Text("verdict: no_effect · undo stopped partway\n" + string.Join('\n', lines));
                    }
                }
                var skipped = all.Count(x => x.Id >= from && !x.Undoable && x.Kind != "checkpoint");
                return ToolResult.Text($"verdict: done · undid {todo.Count} changes" + (skipped > 0 ? $" ({skipped} can't be undone, see list)" : "") +
                                       "\n" + string.Join('\n', lines));
            }
            default: throw new ToolError("bad_args", "undo actions: list, last, to, checkpoint");
        }
    }

    private static string Line(Journal.Entry e) =>
        $"u{e.Id} · {e.Time[11..]} · {e.What} · " + (e.Kind == "checkpoint" ? "checkpoint" : e.Undone ? "undone" : e.Undoable ? "can undo" : "can't undo");

    private static string UndoOne(Journal.Entry e)
    {
        var d = e.Data;
        string S(string k) => d.Str(k) ?? throw new ToolError("broken", $"u{e.Id} lacks {k}");
        string result;
        switch (e.Kind)
        {
            case "file_new":
            {
                var p = S("path");
                if (!File.Exists(p)) { result = $"{p} was already gone"; break; }
                if (Journal.Hash(p) != d.Str("hash"))
                    Approve.Require("move a file to the Recycle Bin", $"{p} was made by Reach but has changed since. Undo moves it to the Recycle Bin.");
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                result = $"{p} moved to the Recycle Bin";
                break;
            }
            case "file_change":
            {
                var p = S("path");
                var backup = S("backup");
                if (!File.Exists(backup)) throw new ToolError("gone", "the backup is gone");
                if (File.Exists(p) && Journal.Hash(p) != d.Str("hash"))
                    Approve.Require("put back an older copy of a file", $"{p} has changed since Reach wrote it. Undo replaces it with the copy from before Reach's change.");
                File.Copy(backup, p, overwrite: true);
                result = $"{p} is back as it was";
                break;
            }
            case "file_move":
            {
                var from = S("from");
                var to = S("to");
                if (File.Exists(from) || Directory.Exists(from)) throw new ToolError("exists", $"something is at {from} again; move it away first");
                if (File.Exists(to)) File.Move(to, from);
                else if (Directory.Exists(to)) Directory.Move(to, from);
                else throw new ToolError("not_found", $"{to} is gone");
                result = $"moved back to {from}";
                break;
            }
            case "file_delete":
            {
                var p = S("path");
                var backup = S("backup");
                if (File.Exists(p)) throw new ToolError("exists", $"a file is at {p} again");
                if (!File.Exists(backup)) throw new ToolError("gone", "the backup is gone; look in the Recycle Bin");
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.Copy(backup, p);
                result = $"{p} is back (it's also still in the Recycle Bin)";
                break;
            }
            case "window":
            {
                var h = new IntPtr(d["hwnd"]!.GetValue<long>());
                if (!Native.IsWindow(h)) throw new ToolError("gone", $"\"{d.Str("title")}\" is closed");
                switch (d.Str("state"))
                {
                    case "max": Native.ShowWindow(h, 3); break;
                    case "min": Native.ShowWindow(h, 6); break;
                    default:
                        DeskTools.Move(h, new Box(d["l"]!.GetValue<int>(), d["t"]!.GetValue<int>(), d["r"]!.GetValue<int>(), d["b"]!.GetValue<int>()));
                        break;
                }
                var top = d["topmost"]?.GetValue<bool>() == true;
                SetWindowPos(h, new IntPtr(top ? -1 : -2), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
                result = $"\"{d.Str("title")}\" is back where it was";
                break;
            }
            case "clipboard":
            {
                var prev = d.Str("previous");
                Exception? err = null;
                var t = new Thread(() =>
                {
                    try { if (prev == null) System.Windows.Forms.Clipboard.Clear(); else System.Windows.Forms.Clipboard.SetText(prev); }
                    catch (Exception x) { err = x; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join(5_000);
                if (err != null) throw new ToolError("busy", "another app is holding the clipboard; try again");
                result = prev == null ? "clipboard emptied (it had no text before)" : "the clipboard has its old text";
                break;
            }
            case "excel_write":
                OfficeTool.PutCells(S("book"), S("sheet"), S("address"), d["cells"] as JsonArray ?? new JsonArray());
                result = $"{d.Str("address")} has its old values (not saved yet)";
                break;
            default:
                throw new ToolError("not_supported", $"u{e.Id} can't be undone");
        }
        Journal.MarkUndone(e.Id);
        return result;
    }
}
