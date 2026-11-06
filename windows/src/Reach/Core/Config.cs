using System.Text.Json;

namespace Reach.Core;

/// <summary>
/// %LOCALAPPDATA%\Reach\config.json: which abilities are on, app rules, folders and approvals (spec/POLICY.md).
/// Written with the defaults the first time, changed from the settings window (reach.exe --settings) or by hand.
/// Read again whenever the file changes, so a switch counts from the next call. The model never sees or changes it.
/// </summary>
public sealed class Config
{
    public bool Office { get; set; } = true;
    public bool Explorer { get; set; } = true;
    public bool Files { get; set; } = true;
    public bool Browser { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Windows { get; set; } = true;
    public bool Processes { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    /// <summary>Off by default: PowerShell commands (each one still asks).</summary>
    public bool Shell { get; set; }
    /// <summary>Off: never the real mouse or keyboard, only background actions (same as --background-only).</summary>
    public bool RealInput { get; set; } = true;
    /// <summary>The tray icon with Pause and Settings (one per PC, whichever Reach starts first).</summary>
    public bool Tray { get; set; } = true;
    public int BrowserPort { get; set; } = 9222;
    public List<string> FileFolders { get; set; } = new() { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music" };
    /// <summary>App rules: a program name ("excel") or words in a window title ("hdfc bank") → allow / read_only / block.</summary>
    public Dictionary<string, string> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ask on the phone (Reach for Android) instead of a box on this PC.</summary>
    public bool PhoneApprovals { get; set; }
    /// <summary>Which linked phone asks (its name); empty = the first one.</summary>
    public string ApprovalPhone { get; set; } = "";
    /// <summary>Phones running Reach for Android that this PC may use (the phone tool) and ask for approvals.</summary>
    public List<PhoneLink> Phones { get; set; } = new();
    /// <summary>The phone ability: the agent here may use the linked phones.</summary>
    public bool Phone { get; set; } = true;
    /// <summary>reach.exe --http listens for: "local" (this PC), "tailscale" (+ your Tailscale devices), "lan" (every network).</summary>
    public string Network { get; set; } = "local";

    // Before P7 one phone was set with these two; read once into Phones.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PhoneUrl { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PhoneToken { get; set; }

    public sealed class PhoneLink
    {
        public string Name { get; set; } = "phone";
        public string Url { get; set; } = "http://127.0.0.1:8765";
        public string Token { get; set; } = "";
    }

    /// <summary>The phone that answers approvals, if any.</summary>
    public PhoneLink? ApprovalLink() =>
        Phones.FirstOrDefault(p => string.Equals(p.Name, ApprovalPhone, StringComparison.OrdinalIgnoreCase)) ?? Phones.FirstOrDefault();

    /// <summary>Tests point this at a temp folder.</summary>
    internal static string? DirOverride;
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reach");
    public static string PathOf => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly object Gate = new();
    private static Config? _current;
    private static DateTime _stamp;
    private static DateTime _checked;

    /// <summary>The settings as they are now; the file is looked at again at most twice a second.</summary>
    public static Config Current
    {
        get
        {
            lock (Gate)
            {
                if (_current != null && DateTime.UtcNow - _checked < TimeSpan.FromMilliseconds(500)) return _current;
                _checked = DateTime.UtcNow;
                var stamp = File.Exists(PathOf) ? File.GetLastWriteTimeUtc(PathOf) : DateTime.MinValue;
                if (_current == null || stamp != _stamp) { _current = Load(); _stamp = File.Exists(PathOf) ? File.GetLastWriteTimeUtc(PathOf) : stamp; }
                return _current;
            }
        }
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(PathOf))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(PathOf), Opts) ?? new Config();
                c.Apps = new Dictionary<string, string>(c.Apps ?? new(), StringComparer.OrdinalIgnoreCase);
                c.FileFolders ??= new();
                c.Phones ??= new();
                if (c.Phones.Count == 0 && !string.IsNullOrWhiteSpace(c.PhoneToken))
                    c.Phones.Add(new PhoneLink { Name = "phone", Url = c.PhoneUrl ?? "http://127.0.0.1:8765", Token = c.PhoneToken });
                c.PhoneUrl = null;
                c.PhoneToken = null;
                return c;
            }
            var d = new Config();
            d.Save();
            return d;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"config.json unreadable ({e.Message}); using defaults");
            return new Config();
        }
    }

    /// <summary>Writes the file (whole, then swapped in, so a reader never sees half of it).</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = PathOf + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, PathOf, overwrite: true);
        lock (Gate) { _current = this; _stamp = File.GetLastWriteTimeUtc(PathOf); _checked = DateTime.UtcNow; }
    }

    /// <summary>
    /// Full paths of the allowed folders. Names like "Documents" mean the user's own, and both copies when OneDrive
    /// has moved it (OneDrive\Documents as Windows sees it, and C:\Users\me\Documents, which apps still save to).
    /// </summary>
    public List<string> FolderPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        foreach (var f in FileFolders)
        {
            if (Path.IsPathRooted(f)) { list.Add(f); continue; }
            var special = f.ToLowerInvariant() switch
            {
                "documents" => Environment.SpecialFolder.MyDocuments,
                "desktop" => Environment.SpecialFolder.Desktop,
                "pictures" => Environment.SpecialFolder.MyPictures,
                "videos" => Environment.SpecialFolder.MyVideos,
                "music" => Environment.SpecialFolder.MyMusic,
                _ => (Environment.SpecialFolder?)null,
            };
            var plain = Path.Combine(home, f);
            if (special is { } sp && Environment.GetFolderPath(sp) is { Length: > 0 } real)
            {
                list.Add(real);
                if (!string.Equals(real.TrimEnd('\\'), plain, StringComparison.OrdinalIgnoreCase) && Directory.Exists(plain)) list.Add(plain);
            }
            else list.Add(plain);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The abilities with a switch, in the order the settings window shows them.</summary>
    public static readonly (string Key, string Label, string About)[] Abilities =
    {
        ("office", "Office", "Excel, Word and classic Outlook. Sending mail asks you."),
        ("browser", "Browser", "Edge or Chrome pages, in a separate Reach profile."),
        ("files", "Files", "Only the folders below. Replacing or deleting asks you."),
        ("windows", "Window layout", "Snap, move and resize windows; virtual desktops."),
        ("explorer", "File Explorer", "Open folders, see what is selected, open files."),
        ("notifications", "Notifications", "Read Windows notifications. Codes are hidden."),
        ("processes", "Programs", "List running programs. Closing one asks you."),
        ("clipboard", "Clipboard", "Read and set the clipboard. Codes are hidden."),
        ("shell", "PowerShell", "Run commands. Every command asks you."),
        ("phone", "Your phones", "Use the phones linked below (Reach for Android), from this PC's agent."),
    };

    /// <summary>Is the tool [name] switched on? Core tools always are.</summary>
    public bool Allows(string name) => name switch
    {
        "office" => Office, "explorer" => Explorer, "files" => Files, "browser" => Browser,
        "notifications" => Notifications, "windows" => Windows, "processes" => Processes,
        "clipboard" => Clipboard, "shell" => Shell, "phone" => Phone && Phones.Count > 0, _ => true,
    };

    public void Set(string name, bool on)
    {
        switch (name)
        {
            case "office": Office = on; break;
            case "explorer": Explorer = on; break;
            case "files": Files = on; break;
            case "browser": Browser = on; break;
            case "notifications": Notifications = on; break;
            case "windows": Windows = on; break;
            case "processes": Processes = on; break;
            case "clipboard": Clipboard = on; break;
            case "shell": Shell = on; break;
            case "phone": Phone = on; break;
        }
    }

    public string Summary() =>
        "abilities: " + string.Join(", ", Abilities.Select(a => $"{a.Key} {(Allows(a.Key) ? "on" : "off")}")) +
        $" · real mouse/keyboard {(RealInput ? "on" : "off")} · approvals on {(PhoneApprovals ? "the phone" : "this PC")}" +
        " (the user changes these in Reach settings; reconnect to see newly allowed tools)";
}
