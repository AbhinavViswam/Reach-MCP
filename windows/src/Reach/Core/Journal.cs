using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Reach.Mcp;

namespace Reach.Core;

/// <summary>
/// The undo journal (spec/CONTRACT.md §6.2): every change Reach makes that can be put back, with what is needed to put
/// it back (old file contents, the window's old place, the old clipboard, old cell values). Things that can't be
/// undone (mail sent, a command run, a program closed) are listed too, so the agent and the user know.
/// %LOCALAPPDATA%\Reach\undo\journal.jsonl, backups next to it. Kept on this PC; old backups are pruned.
/// </summary>
public static class Journal
{
    public static string Dir => Path.Combine(Config.Dir, "undo");
    private static string PathOf => Path.Combine(Dir, "journal.jsonl");
    private static string Backups => Path.Combine(Dir, "backups");
    private static readonly Mutex Lock = new(false, @"Local\ReachJournal");
    private const long MaxBackup = 200L * 1024 * 1024;
    private const int Keep = 300;

    public sealed record Entry(int Id, string Time, string Kind, string What, JsonObject Data, bool Undoable, bool Undone);

    /// <summary>Adds a change; returns its number (u7 in replies).</summary>
    public static int Add(string kind, string what, JsonObject data, bool undoable = true) => Locked(() =>
    {
        var all = ReadRaw();
        var id = all.Count == 0 ? 1 : all.Max(e => e.Id) + 1;
        Append(new JsonObject
        {
            ["id"] = id, ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["kind"] = kind, ["what"] = what,
            ["data"] = data, ["undoable"] = undoable,
        });
        return id;
    });

    public static void MarkUndone(int id) => Locked(() => { Append(new JsonObject { ["undone"] = id }); return 0; });

    /// <summary>A copy of [file] kept for undo, or null when it is too big to keep.</summary>
    public static string? Backup(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length > MaxBackup) return null;
        Directory.CreateDirectory(Backups);
        var copy = Path.Combine(Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28] + Path.GetExtension(file));
        File.Copy(file, copy);
        return copy;
    }

    public static string Hash(string file)
    {
        if (!File.Exists(file)) return "";
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s))[..16];
    }

    /// <summary>All entries, newest first, with undone ones marked.</summary>
    public static List<Entry> All() => Locked(ReadRaw);

    private static List<Entry> ReadRaw()
    {
        var list = new List<Entry>();
        var undone = new HashSet<int>();
        if (!File.Exists(PathOf)) return list;
        foreach (var l in File.ReadAllLines(PathOf))
        {
            try
            {
                if (JsonNode.Parse(l) is not JsonObject o) continue;
                if (o["undone"] is JsonValue u) { undone.Add(u.GetValue<int>()); continue; }
                list.Add(new Entry(o["id"]!.GetValue<int>(), o.Str("t") ?? "", o.Str("kind") ?? "", o.Str("what") ?? "",
                    o["data"] as JsonObject ?? new JsonObject(), o["undoable"]?.GetValue<bool>() ?? true, false));
            }
            catch { }
        }
        return list.Select(e => e with { Undone = undone.Contains(e.Id) }).OrderByDescending(e => e.Id).ToList();
    }

    private static void Append(JsonObject line)
    {
        Directory.CreateDirectory(Dir);
        File.AppendAllText(PathOf, line.ToJsonString() + "\n");
        Prune();
    }

    /// <summary>Keeps the newest entries and the backups they use.</summary>
    private static void Prune()
    {
        var lines = File.ReadAllLines(PathOf);
        var entries = lines.Count(l => l.Contains("\"id\""));
        if (entries <= Keep + 50) return;
        var all = ReadRaw();
        var keepIds = all.Take(Keep).Select(e => e.Id).ToHashSet();
        var kept = lines.Where(l =>
        {
            try
            {
                var o = JsonNode.Parse(l) as JsonObject;
                return o?["id"] is JsonValue v ? keepIds.Contains(v.GetValue<int>()) : o?["undone"] is JsonValue u && keepIds.Contains(u.GetValue<int>());
            }
            catch { return false; }
        }).ToArray();
        File.WriteAllLines(PathOf, kept);
        var used = all.Take(Keep).Select(e => e.Data.Str("backup")).Where(b => b != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Backups))
            foreach (var f in Directory.GetFiles(Backups))
                if (!used.Contains(f)) try { File.Delete(f); } catch { }
    }

    private static T Locked<T>(Func<T> f)
    {
        var got = false;
        try { got = Lock.WaitOne(3_000); } catch (AbandonedMutexException) { got = true; }
        try { return f(); }
        finally { if (got) Lock.ReleaseMutex(); }
    }
}
