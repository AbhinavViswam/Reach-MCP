using System.Text.Json.Nodes;
using Reach.Mcp;

namespace Reach.Core;

public enum AppRule { Allow, ReadOnly, Block }

/// <summary>
/// App rules (spec/POLICY.md): built-in blocks for password managers and banking windows, then the user's own rules
/// from config.json, which win. A rule names a program ("keepass") or words in a window title ("hdfc bank").
/// </summary>
public static class Privacy
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane", "enpass", "roboform", "nordpass",
        "proton pass", "protonpass", "authy desktop", "authy",
    };

    private static readonly string[] BlockedTitleWords =
    {
        "net banking", "netbanking", "online banking", "internet banking", "bank login", "paypal", "password manager",
        "bitwarden", "1password", "keepass", "lastpass", "authenticator",
    };

    public const string Builtin = "password managers (1Password, Bitwarden, KeePass, LastPass…), authenticators, and " +
                                  "banking or PayPal windows";

    public static AppRule Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "block" or "blocked" => AppRule.Block,
        "read_only" or "readonly" or "read only" or "read-only" => AppRule.ReadOnly,
        _ => AppRule.Allow,
    };

    public static string Word(AppRule r) => r switch { AppRule.Block => "block", AppRule.ReadOnly => "read_only", _ => "allow" };

    /// <summary>The rule for a window of [process] titled [title]. [user]: the user's rules (config.json "apps").</summary>
    public static AppRule Rule(string? process, string? title, IReadOnlyDictionary<string, string> user)
    {
        // The user's rules first: a program name exactly, then words in the title (longest rule first).
        if (!string.IsNullOrEmpty(process) && user.TryGetValue(process, out var byName)) return Parse(byName);
        if (!string.IsNullOrEmpty(title))
        {
            var t = title.ToLowerInvariant();
            foreach (var (k, v) in user.OrderByDescending(kv => kv.Key.Length))
                if (k.Trim().Length >= 3 && t.Contains(k.Trim().ToLowerInvariant())) return Parse(v);
        }
        if (!string.IsNullOrEmpty(process) && BlockedProcesses.Contains(process)) return AppRule.Block;
        if (!string.IsNullOrEmpty(title))
        {
            var t = title.ToLowerInvariant();
            if (BlockedTitleWords.Any(t.Contains)) return AppRule.Block;
        }
        return AppRule.Allow;
    }

    public static AppRule Rule(string? process, string? title = null) => Rule(process, title, Config.Current.Apps);

    public static bool IsBlocked(string? process, string? title = null) => Rule(process, title) == AppRule.Block;

    /// <summary>For actions: refuses in read-only apps (reading, and scrolling to read, stay allowed).</summary>
    public static void RequireCanAct(string? process, string? title, string name)
    {
        var r = Rule(process, title);
        if (r == AppRule.Block) throw new ToolError("blocked_app", $"{name} is blocked by Reach privacy rules");
        if (r == AppRule.ReadOnly)
            throw new ToolError("read_only_app", $"{name} is read-only in Reach: it can be read but not used. The user can change this in Reach settings (App rules)");
    }
}

/// <summary>
/// The kill switch: while %LOCALAPPDATA%\Reach\paused exists, every Reach on this PC refuses calls, and a running
/// action is stopped (the server kills its worker). Set from the tray, the hotkey or the settings window.
/// </summary>
public static class Pause
{
    private static string PathOf => Path.Combine(Config.Dir, "paused");

    public static bool IsOn => File.Exists(PathOf);

    public static void Set(bool on)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            if (on) File.WriteAllText(PathOf, DateTime.Now.ToString("O"));
            else if (File.Exists(PathOf)) File.Delete(PathOf);
        }
        catch (Exception e) { Console.Error.WriteLine("pause flag: " + e.Message); }
    }

    public const string Hint = "the user paused Reach on this PC; nothing was done. Wait for them to resume it, don't retry now";

    public static void ThrowIfOn()
    {
        if (IsOn) throw new ToolError("paused", Hint);
    }
}

/// <summary>
/// The activity log (spec/POLICY.md): every call, its result and every approval, one JSON line each in
/// %LOCALAPPDATA%\Reach\audit.jsonl. Stays on this PC; shown in Reach settings › Activity. Codes are masked and long
/// text is cut, so the log never holds more than the agent saw.
/// </summary>
public static class Audit
{
    public static string PathOf => Path.Combine(Config.Dir, "audit.jsonl");
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly Mutex Lock = new(false, @"Local\ReachAudit");

    public static void Call(string tool, JsonObject args, ToolResult r, long ms, string via)
    {
        var first = r.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        var line = new JsonObject
        {
            ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["kind"] = "call",
            ["tool"] = tool,
            ["args"] = Summary(args),
            ["ok"] = !r.IsError,
            ["result"] = Clip(Mask.Apply(first.Split('\n')[0]).Text, 200),
            ["ms"] = ms,
            ["via"] = via,
        };
        Write(line);
    }

    public static void Approval(string what, string detail, bool allowed, string where)
    {
        Write(new JsonObject
        {
            ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["kind"] = "approval",
            ["tool"] = what,
            ["args"] = Clip(Mask.Apply(detail.Replace('\n', ' ')).Text, 300),
            ["ok"] = allowed,
            ["result"] = (allowed ? "allowed" : "denied") + " on " + where,
        });
    }

    public static void Note(string what)
    {
        Write(new JsonObject { ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["kind"] = "note", ["tool"] = what, ["ok"] = true });
    }

    /// <summary>The arguments in short: each value cut to 120 characters, codes hidden.</summary>
    public static string Summary(JsonObject args)
    {
        var parts = new List<string>();
        foreach (var (k, v) in args)
        {
            var s = v switch
            {
                null => "null",
                JsonValue jv when jv.TryGetValue<string>(out var str) => "\"" + Clip(str, 120) + "\"",
                _ => Clip(v.ToJsonString(), 120),
            };
            parts.Add($"{k}={s}");
        }
        return Clip(Mask.Apply(string.Join(" ", parts)).Text, 400);
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static void Write(JsonObject line)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            var got = false;
            try { got = Lock.WaitOne(2_000); } catch (AbandonedMutexException) { got = true; }
            try
            {
                var f = new FileInfo(PathOf);
                if (f.Exists && f.Length > MaxBytes) File.Move(PathOf, Path.Combine(Config.Dir, "audit.1.jsonl"), overwrite: true);
                File.AppendAllText(PathOf, line.ToJsonString() + "\n");
            }
            finally { if (got) Lock.ReleaseMutex(); }
        }
        catch (Exception e) { Console.Error.WriteLine("audit: " + e.Message); }
    }

    public sealed record Entry(string Time, string Kind, string Tool, string Args, bool Ok, string Result, long Ms, string Via);

    /// <summary>The newest [max] entries, newest first.</summary>
    public static List<Entry> Read(int max = 500)
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(PathOf)) return list;
            string[] lines;
            using (var fs = new FileStream(PathOf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var l in lines.Reverse().Take(max))
            {
                try
                {
                    if (JsonNode.Parse(l) is not JsonObject o) continue;
                    list.Add(new Entry(o.Str("t") ?? "", o.Str("kind") ?? "", o.Str("tool") ?? "", o.Str("args") ?? "",
                        o["ok"]?.GetValue<bool>() ?? false, o.Str("result") ?? "", o["ms"]?.GetValue<long>() ?? 0, o.Str("via") ?? ""));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static void Clear()
    {
        try { File.Delete(PathOf); File.Delete(Path.Combine(Config.Dir, "audit.1.jsonl")); } catch { }
    }
}
