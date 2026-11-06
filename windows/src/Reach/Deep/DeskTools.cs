using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Reach.Core;
using Reach.Mcp;
using Reach.Worker;
using static Reach.Worker.Native;

namespace Reach.Deep;

/// <summary>Where a snapped window goes inside a monitor's work area. Plain code, tested.</summary>
public static class Snap
{
    public static Box Place(string where, Box work) => where.ToLowerInvariant() switch
    {
        "left" => work with { Right = work.Left + work.Width / 2 },
        "right" => work with { Left = work.Left + work.Width / 2 },
        "top" => work with { Bottom = work.Top + work.Height / 2 },
        "bottom" => work with { Top = work.Top + work.Height / 2 },
        "top_left" => new Box(work.Left, work.Top, work.Left + work.Width / 2, work.Top + work.Height / 2),
        "top_right" => new Box(work.Left + work.Width / 2, work.Top, work.Right, work.Top + work.Height / 2),
        "bottom_left" => new Box(work.Left, work.Top + work.Height / 2, work.Left + work.Width / 2, work.Bottom),
        "bottom_right" => new Box(work.Left + work.Width / 2, work.Top + work.Height / 2, work.Right, work.Bottom),
        "center" => new Box(work.Left + work.Width / 6, work.Top + work.Height / 8, work.Right - work.Width / 6, work.Bottom - work.Height / 8),
        _ => throw new ToolError("bad_args", "snap: left, right, top, bottom, top_left, top_right, bottom_left, bottom_right, center"),
    };
}

internal static class DeskTools
{
    // ============================================================================================ windows

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    public static ToolResult Windows(JsonObject a, Func<string?, Win> pick, bool backgroundOnly)
    {
        var action = a.Str("action") ?? throw new ToolError("bad_args", "action is needed");
        if (action == "save_layout" || action == "restore_layout") return Layout(action, a.Str("name") ?? "default");
        if (action is "desktop_next" or "desktop_prev")
        {
            if (backgroundOnly) throw new ToolError("needs_foreground", "switching virtual desktops needs the keyboard");
            Input.WaitForUserPause();
            Input.Combo(action == "desktop_next" ? "ctrl+win+right" : "ctrl+win+left");
            return ToolResult.Text($"verdict: done · switched to the {(action == "desktop_next" ? "next" : "previous")} virtual desktop");
        }
        var w = pick(a.Str("window"));
        if (action is "snap" or "move" or "maximize" or "minimize" or "restore" or "on_top") Undo.WindowBefore(w.Handle, $"{action} \"{w.Title}\"");
        switch (action)
        {
            case "snap":
            {
                var work = Screen.FromHandle(w.Handle).WorkingArea;
                var to = Snap.Place(a.Str("to") ?? "left", new Box(work.Left, work.Top, work.Right, work.Bottom));
                Move(w.Handle, to);
                return ToolResult.Text($"verdict: done · {w.Id} \"{w.Title}\" snapped {a.Str("to")} ({to.Width}×{to.Height} at {to.Left},{to.Top})");
            }
            case "move":
            {
                var b = Desktop.Bounds(w.Handle);
                var x = a.Int("x") ?? b.Left;
                var y = a.Int("y") ?? b.Top;
                var to = new Box(x, y, x + (a.Int("width") ?? b.Width), y + (a.Int("height") ?? b.Height));
                Move(w.Handle, to);
                return ToolResult.Text($"verdict: done · {w.Id} now {to.Width}×{to.Height} at {to.Left},{to.Top}");
            }
            case "maximize": ShowWindow(w.Handle, 3); return ToolResult.Text($"verdict: done · maximized {w.Id}");
            case "minimize": ShowWindow(w.Handle, 6); return ToolResult.Text($"verdict: done · minimized {w.Id}");
            case "restore": ShowWindow(w.Handle, SW_RESTORE); return ToolResult.Text($"verdict: done · restored {w.Id}");
            case "on_top":
            {
                var on = a.Bool("on") != false;
                SetWindowPos(w.Handle, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return ToolResult.Text($"verdict: done · {w.Id} {(on ? "stays on top" : "no longer on top")}");
            }
            default:
                throw new ToolError("bad_args", "actions: snap, move, maximize, minimize, restore, on_top, save_layout, restore_layout, desktop_next, desktop_prev");
        }
    }

    /// <summary>Moves so the window's visible edges (not its invisible resize border) land on [to].</summary>
    internal static void Move(IntPtr h, Box to)
    {
        if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, SW_RESTORE);
        GetWindowRect(h, out var full);
        var vis = Desktop.Bounds(h);
        int l = vis.Left - full.Left, t = vis.Top - full.Top, r = full.Right - vis.Right, b = full.Bottom - vis.Bottom;
        SetWindowPos(h, IntPtr.Zero, to.Left - l, to.Top - t, to.Width + l + r, to.Height + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);

    private static string LayoutsPath => Path.Combine(Config.Dir, "layouts.json");

    private static ToolResult Layout(string action, string name)
    {
        var all = File.Exists(LayoutsPath) ? JsonNode.Parse(File.ReadAllText(LayoutsPath)) as JsonObject ?? new JsonObject() : new JsonObject();
        if (action == "save_layout")
        {
            var arr = new JsonArray();
            foreach (var w in Desktop.Windows().Where(w => !w.Minimized))
                arr.Add(new JsonObject { ["app"] = w.App, ["title"] = w.Title, ["l"] = w.Rect.Left, ["t"] = w.Rect.Top, ["r"] = w.Rect.Right, ["b"] = w.Rect.Bottom });
            all[name] = arr;
            Directory.CreateDirectory(Config.Dir);
            File.WriteAllText(LayoutsPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return ToolResult.Text($"verdict: done · saved layout \"{name}\" ({arr.Count} windows)");
        }
        if (all[name] is not JsonArray saved) throw new ToolError("not_found", $"no layout \"{name}\"; saved: {string.Join(", ", all.Select(kv => kv.Key))}");
        var open = Desktop.Windows();
        var moved = 0;
        foreach (var s in saved.OfType<JsonObject>())
        {
            // Same app, same title first; then the same app.
            var w = open.FirstOrDefault(o => o.App == s.Str("app") && o.Title == s.Str("title")) ?? open.FirstOrDefault(o => o.App == s.Str("app"));
            if (w == null) continue;
            open.Remove(w);
            Move(w.Handle, new Box(s.Int("l") ?? 0, s.Int("t") ?? 0, s.Int("r") ?? 0, s.Int("b") ?? 0));
            moved++;
        }
        return ToolResult.Text($"verdict: done · layout \"{name}\": moved {moved} of {saved.Count} windows (the rest aren't open)");
    }

    // ============================================================================================ explorer

    public static ToolResult Explorer(JsonObject a, Config cfg)
    {
        var action = a.Str("action") ?? "list";
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        switch (action)
        {
            case "list":
            {
                var rows = new List<string>();
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        string path = w.Document.Folder.Self.Path;
                        var sel = new List<string>();
                        foreach (dynamic it in w.Document.SelectedItems()) sel.Add((string)it.Name);
                        rows.Add($"{Desktop.IdOf(new IntPtr((long)w.HWND))} · {path}" + (sel.Count > 0 ? $" · selected: {string.Join(", ", sel.Take(20))}" : ""));
                    }
                    catch { /* Internet Explorer windows and others without a folder */ }
                }
                return ToolResult.Text(rows.Count == 0 ? "no File Explorer windows open" : string.Join('\n', rows));
            }
            case "open":
            {
                var p = Fence.Resolve(cfg.FolderPaths(), a.Str("path") ?? throw new ToolError("bad_args", "open needs path"));
                if (File.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
                else if (Directory.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
                else throw new ToolError("not_found", $"{p} doesn't exist");
                return ToolResult.Text($"verdict: done · File Explorer opened at {p}");
            }
            case "launch":
            {
                // Opens a file with its usual app (a document in Word, a photo in Photos).
                var p = Fence.Resolve(cfg.FolderPaths(), a.Str("path") ?? throw new ToolError("bad_args", "launch needs path"));
                if (!File.Exists(p)) throw new ToolError("not_found", $"{p} doesn't exist");
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".lnk" or ".scr")
                    Approve.Require("run a program", p);
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                return ToolResult.Text($"verdict: done · opened {Path.GetFileName(p)} with its app (observe to see it)");
            }
            default:
                throw new ToolError("bad_args", "explorer actions: list, open, launch");
        }
    }

    // ============================================================================================ files

    public static ToolResult Files(JsonObject a, Config cfg)
    {
        var allowed = cfg.FolderPaths();
        var action = a.Str("action") ?? throw new ToolError("bad_args", "action is needed");
        var limit = Math.Clamp(a.Int("limit") ?? 50, 1, 500);
        string P(string k) => Fence.Resolve(allowed, a.Str(k) ?? throw new ToolError("bad_args", $"{action} needs {k}"));
        switch (action)
        {
            case "list":
            {
                if (a.Str("path") == null) return ToolResult.Text("folders: " + string.Join(", ", allowed));
                var d = P("path");
                if (!Directory.Exists(d)) throw new ToolError("not_found", $"{d} is not a folder");
                var pattern = a.Str("pattern") ?? "*";
                var items = new DirectoryInfo(d).EnumerateFileSystemInfos(pattern).OrderByDescending(f => f.LastWriteTime).Take(limit).ToList();
                return ToolResult.Text($"{d}\n" + string.Join('\n', items.Select(f =>
                    (f is DirectoryInfo ? "folder " : "file ") + f.Name + (f is FileInfo fi ? $" · {Size(fi.Length)}" : "") + $" · {f.LastWriteTime:yyyy-MM-dd HH:mm}")));
            }
            case "find":
            {
                var name = a.Str("pattern") ?? throw new ToolError("bad_args", "find needs pattern, e.g. *.pdf or report*");
                var roots = a.Str("path") != null ? new List<string> { P("path") } : allowed.Where(Directory.Exists).ToList();
                var hits = new List<string>();
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                foreach (var r in roots)
                    foreach (var f in Directory.EnumerateFileSystemEntries(r, name, opts)) { hits.Add(f); if (hits.Count >= limit) break; }
                return ToolResult.Text(hits.Count == 0 ? $"nothing matches {name}" : string.Join('\n', hits));
            }
            case "read":
            {
                var f = P("path");
                if (!File.Exists(f)) throw new ToolError("not_found", $"{f} not found");
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
                {
                    using var bmp = new System.Drawing.Bitmap(f);
                    var s = Capture.Encode(bmp, 0, 0);
                    return new ToolResult(new[] { Content.Of($"{f} · {bmp.Width}×{bmp.Height}"), Content.Image(s.Base64, "image/jpeg", s.Width, s.Height) });
                }
                using var fs = File.OpenRead(f);
                var buf = new byte[Math.Min(fs.Length, 64_000)];
                fs.ReadExactly(buf);
                var text = Encoding.UTF8.GetString(buf);
                if (text.Count(c => c == '�' || c == '\0') > text.Length / 20)
                    throw new ToolError("not_supported", $"{Path.GetFileName(f)} is not a text file (Word/Excel files: use the office tool)");
                var (t, masked) = Mask.Apply(text);
                return ToolResult.Text($"{f} · {Size(fs.Length)}{(fs.Length > 64_000 ? " (first 64 KB)" : "")}\n{ViewText.Fence}\n{t}" + (masked ? "\n" + Core.Mask.Note : ""));
            }
            case "write":
            {
                var f = P("path");
                var text = a.Str("text") ?? throw new ToolError("bad_args", "write needs text");
                var append = a.Bool("append") == true;
                if (File.Exists(f) && !append) Approve.Require("replace a file", $"{f} ({Size(new FileInfo(f).Length)}) will be overwritten.");
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                var existed = File.Exists(f);
                var backup = existed ? Journal.Backup(f) : null;
                if (append) File.AppendAllText(f, text); else File.WriteAllText(f, text);
                Undo.FileWritten(f, existed, backup);
                return ToolResult.Text($"verdict: done · {(append ? "added to" : "wrote")} {f}");
            }
            case "move" or "copy" or "rename":
            {
                var from = P("path");
                var to = action == "rename" ? Path.Combine(Path.GetDirectoryName(from)!, a.Str("to") ?? throw new ToolError("bad_args", "rename needs to (new name)")) : P("to");
                Fence.Resolve(allowed, to);
                if (File.Exists(to) || Directory.Exists(to)) throw new ToolError("exists", $"{to} already exists; pick another name");
                if (File.Exists(from)) { if (action == "copy") File.Copy(from, to); else File.Move(from, to); }
                else if (Directory.Exists(from) && action != "copy") Directory.Move(from, to);
                else throw new ToolError("not_found", $"{from} not found (folders can be moved, not copied)");
                if (action == "copy") Undo.FileCopied(to); else Undo.FileMoved(from, to);
                return ToolResult.Text($"verdict: done · {action} → {to}");
            }
            case "delete":
            {
                var f = P("path");
                if (allowed.Any(d => string.Equals(Path.GetFullPath(d).TrimEnd('\\'), f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    throw new ToolError("blocked", "Reach won't delete a whole main folder");
                Approve.Require("move to the Recycle Bin", f);
                var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
                var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
                if (File.Exists(f))
                {
                    var backup = Journal.Backup(f);
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, ui, recycle);
                    Undo.FileDeleted(f, backup, folder: false);
                }
                else if (Directory.Exists(f))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(f, ui, recycle);
                    Undo.FileDeleted(f, null, folder: true);
                }
                else throw new ToolError("not_found", $"{f} not found");
                return ToolResult.Text($"verdict: done · {f} is in the Recycle Bin (restore it from there if needed)");
            }
            default:
                throw new ToolError("bad_args", "files actions: list, find, read, write, move, copy, rename, delete");
        }
    }

    private static string Size(long n) => n < 1024 ? $"{n} B" : n < 1 << 20 ? $"{n / 1024} KB" : $"{n / 1048576.0:0.0} MB";

    // ============================================================================================ notifications

    private static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");

    public sealed record Toast(long Id, string App, DateTime When, string Text);

    /// <summary>Windows' own notification store, read only (no app needs to be told).</summary>
    public static List<Toast> Toasts(int limit, long afterId = 0)
    {
        if (!File.Exists(DbPath)) throw new ToolError("not_supported", "this Windows has no notification store");
        var list = new List<Toast>();
        using var db = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Cache=Shared");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Payload FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                          "WHERE n.Type = 'toast' AND n.Id > $after ORDER BY n.ArrivalTime DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var payload = r.IsDBNull(3) ? "" : r[3] is byte[] b ? Encoding.UTF8.GetString(b) : r.GetString(3);
            string text;
            try { text = string.Join(" · ", XDocument.Parse(payload).Descendants("text").Select(x => x.Value.Trim()).Where(x => x.Length > 0)); }
            catch { continue; }
            if (text.Length == 0) continue;
            var app = r.GetString(1);
            list.Add(new Toast(r.GetInt64(0), app, DateTime.FromFileTime(r.GetInt64(2)), text));
        }
        return list;
    }

    private static string AppName(string id)
    {
        // "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Chrome", "{GUID}\app.exe" → something readable.
        var s = id.Split('!')[0].Split('\\').Last();
        s = s.Split('_')[0];
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    public static ToolResult Notifications(JsonObject a)
    {
        var action = a.Str("action") ?? "list";
        var app = a.Str("app")?.ToLowerInvariant();
        var reveal = a.Bool("reveal") == true;
        string Lines(IEnumerable<Toast> ts)
        {
            var rows = ts.Where(t => !Privacy.IsBlocked(AppName(t.App), t.Text)).Select(t => $"{AppName(t.App)} · {t.When:ddd d MMM HH:mm} · {t.Text}").ToList();
            var masked = rows.Select(Mask.Apply).ToList();
            var any = masked.Any(m => m.Masked);
            if (any && reveal) Approve.Require("see hidden codes in your notifications", "Your agent wants to read one-time codes or card numbers in your notifications.");
            return ViewText.Fence + "\n" + string.Join('\n', any && reveal ? rows : masked.Select(m => m.Text)) + (any && !reveal ? "\n" + Core.Mask.Note : "");
        }
        bool Mine(Toast t) => app == null || AppName(t.App).ToLowerInvariant().Contains(app);
        switch (action)
        {
            case "list":
            {
                var ts = Toasts(Math.Clamp(a.Int("limit") ?? 15, 1, 50) * 3).Where(Mine).Take(Math.Clamp(a.Int("limit") ?? 15, 1, 50)).ToList();
                return ToolResult.Text(ts.Count == 0 ? "no notifications" : $"{ts.Count} notifications, newest first\n{Lines(ts)}");
            }
            case "wait":
            {
                var last = Toasts(1).FirstOrDefault()?.Id ?? 0;
                var timeout = Math.Clamp(a.Int("timeout_ms") ?? 30_000, 1_000, 60_000);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeout)
                {
                    Thread.Sleep(700);
                    var fresh = Toasts(10, last).Where(Mine).ToList();
                    if (fresh.Count > 0) return ToolResult.Text("new notification\n" + Lines(fresh));
                }
                return ToolResult.Text($"verdict: timeout · no new notification{(app != null ? " from " + app : "")} in {timeout / 1000} s");
            }
            default:
                throw new ToolError("bad_args", "notifications actions: list, wait (Windows doesn't let other apps press notification buttons)");
        }
    }

    // ============================================================================================ processes

    public static ToolResult Processes(JsonObject a)
    {
        var action = a.Str("action") ?? "list";
        switch (action)
        {
            case "list":
            {
                var q = a.Str("query")?.ToLowerInvariant();
                var rows = Process.GetProcesses()
                    .Select(p => { try { return (p.ProcessName, p.Id, Mem: p.WorkingSet64, Title: p.MainWindowTitle); } catch { return (p.ProcessName, p.Id, Mem: 0L, Title: ""); } })
                    .Where(p => q == null || p.ProcessName.ToLowerInvariant().Contains(q) || p.Title.ToLowerInvariant().Contains(q))
                    .OrderByDescending(p => p.Mem).Take(Math.Clamp(a.Int("limit") ?? 25, 1, 200));
                return ToolResult.Text("biggest first (memory)\n" + string.Join('\n', rows.Select(p => $"{p.ProcessName} · pid {p.Id} · {p.Mem / 1048576} MB" + (p.Title.Length > 0 ? $" · \"{ViewText.Clip(p.Title, 60)}\"" : ""))));
            }
            case "kill":
            {
                var pid = a.Int("pid") ?? throw new ToolError("bad_args", "kill needs pid (from list)");
                Process p;
                try { p = Process.GetProcessById(pid); } catch { throw new ToolError("not_found", $"no process {pid}"); }
                var name = p.ProcessName;
                if (name.Equals("reach", StringComparison.OrdinalIgnoreCase) || p.SessionId == 0)
                    throw new ToolError("blocked", "Reach won't end itself or system processes");
                Approve.Require($"force-close {name}", $"{name} (pid {pid}){(p.MainWindowTitle.Length > 0 ? " · " + p.MainWindowTitle : "")} will be closed. Unsaved work in it is lost.");
                p.Kill(entireProcessTree: true);
                Undo.CantUndo($"closed {name} (pid {pid})");
                return ToolResult.Text($"verdict: done · ended {name}");
            }
            default:
                throw new ToolError("bad_args", "processes actions: list, kill");
        }
    }

    // ============================================================================================ clipboard

    public static ToolResult Clipboard(JsonObject a)
    {
        var action = a.Str("action") ?? "get";
        string? got = null;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                if (action == "set")
                {
                    var text = a.Str("text") ?? throw new ToolError("bad_args", "set needs text");
                    Undo.ClipboardBefore(System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);
                    System.Windows.Forms.Clipboard.SetText(text);
                }
                else got = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null;
            }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(5_000);
        if (err is ToolError te) throw te;
        if (err != null) throw new ToolError("busy", "another app is holding the clipboard; try again");
        if (action == "set") return ToolResult.Text($"verdict: done · clipboard set ({a.Str("text")!.Length} characters)");
        if (got == null) return ToolResult.Text("clipboard has no text");
        var (masked, any) = Mask.Apply(got.Length > 20_000 ? got[..20_000] : got);
        if (any && a.Bool("reveal") == true)
        {
            Approve.Require("see hidden codes on the clipboard", "Your agent wants the clipboard's one-time code or card number.");
            return ToolResult.Text($"clipboard\n{ViewText.Fence}\n{got}");
        }
        return ToolResult.Text($"clipboard\n{ViewText.Fence}\n{masked}" + (any ? "\n" + Core.Mask.Note : ""));
    }

    // ============================================================================================ shell

    /// <summary>
    /// PowerShell, off unless the user turns it on in config.json. Every command is shown to the user and needs
    /// Allow; every command and its result go to shell.log.
    /// </summary>
    public static ToolResult Shell(JsonObject a)
    {
        var cmd = a.Str("command") ?? throw new ToolError("bad_args", "command is needed");
        var timeout = Math.Clamp(a.Int("timeout_s") ?? 30, 1, 120);
        Approve.Require("run a PowerShell command", cmd);
        Undo.CantUndo("ran PowerShell: " + (cmd.Length > 100 ? cmd[..100] + "…" : cmd));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
        p.StandardInput.WriteLine(cmd);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var finished = p.WaitForExit(timeout * 1000);
        if (!finished) { try { p.Kill(true); } catch { } }
        var stdout = outTask.Wait(2_000) ? outTask.Result : "";
        var stderr = errTask.Wait(2_000) ? errTask.Result : "";
        var result = (stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "")).Trim();
        if (result.Length > 20_000) result = result[..20_000] + "\n… (cut at 20000 characters)";
        try
        {
            File.AppendAllText(Path.Combine(Config.Dir, "shell.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} exit {(finished ? p.ExitCode.ToString() : "timeout")}\n> {cmd}\n{result}\n");
        }
        catch { }
        var (t, masked) = Mask.Apply(result);
        var head = finished ? $"exit code {p.ExitCode}" : $"verdict: timeout · stopped after {timeout} s";
        return ToolResult.Text($"{head}\n{ViewText.Fence}\n{t}" + (masked ? "\n" + Core.Mask.Note : ""));
    }
}
