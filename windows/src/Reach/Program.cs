using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Reach.Core;
using Reach.Mcp;
using Reach.Server;

namespace Reach;

/// <summary>
/// reach.exe
///   (no arguments)        MCP over stdio, for agents that start the server themselves (Claude Code, Cursor, …)
///                         (double-clicked, with nobody on stdin: opens the settings window instead)
///   --http [--port N]     MCP over HTTP on 127.0.0.1:8766 with a token (--tailscale: also your Tailscale devices;
///                         --lan: every network, use with care; default from settings)
///   --background-only     never use the real mouse/keyboard; reply needs_foreground instead
///   --settings            the settings window (abilities, app rules, approvals, activity, pause)
///   --pause / --resume    the kill switch from a script or shortcut
///   --token [--new]       print the HTTP token (make a new one)
///   --selftest            read the front window once and print it (reads only, changes nothing)
///   --worker              internal: the UI worker the server starts
/// </summary>
public static class Program
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    [MTAThread]
    public static async Task<int> Main(string[] args)
    {
        var has = (string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        var backgroundOnly = has("--background-only");

        if (has("--worker")) return Worker.WorkerMain.Run(backgroundOnly);
        if (has("--selftest")) return Worker.WorkerMain.SelfTest();
        if (has("--token"))
        {
            Console.WriteLine(has("--new") ? Token.New() : Token.Get());
            return 0;
        }
        if (has("--pause") || has("--resume"))
        {
            Pause.Set(has("--pause"));
            Audit.Note(has("--pause") ? "paused by the user (command line)" : "resumed by the user (command line)");
            Console.WriteLine(Pause.IsOn ? "Reach paused" : "Reach resumed");
            return 0;
        }
        if (has("--help") || has("-h"))
        {
            Console.WriteLine("reach.exe [--http [--port N] [--tailscale | --lan]] [--background-only] | --settings | --pause | --resume | --token [--new] | --selftest");
            return 0;
        }
        if (has("--ui-snapshot"))
        {
            // Development: draws the windows into PNGs off-screen (docs and checks), nothing is shown.
            var dir = args.SkipWhile(a => !a.Equals("--ui-snapshot", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? ".";
            return RunWindow(() => new Ui.Snapshot(dir));
        }
        if (has("--settings") || (args.Length == 0 && !Console.IsInputRedirected))
        {
            // Double-clicked: an stdio server on an empty console helps nobody. Show settings instead.
            HideOwnConsole();
            return RunWindow(() => new Ui.SettingsForm());
        }

        Config.Load(); // makes config.json with the defaults on first run
        using var worker = new WorkerHost(backgroundOnly ? new[] { "--background-only" } : Array.Empty<string>());
        var via = has("--http") ? "http" : "stdio";
        McpHandler? self = null;
        var phones = new PhoneHub();

        // One call to the PC, as the agent's calls and flow steps both make them: pause, switches, worker, activity log.
        async Task<ToolResult> Exec(string name, System.Text.Json.Nodes.JsonObject a, CancellationToken ct, string how)
        {
            var sw = Stopwatch.StartNew();
            var args0 = (System.Text.Json.Nodes.JsonObject)a.DeepClone();
            ToolResult r;
            if (Pause.IsOn) r = ToolResult.Error("paused", Pause.Hint);
            else if (Catalog.Find(name) is not { } def) r = ToolResult.Error("not_found", $"no tool '{name}'");
            else if (name == "phone")
            {
                // Runs here in the server: the phone is another device over HTTP, not a window.
                if (!Config.Current.Allows("phone")) r = ToolResult.Error("feature_off", "no phone is linked or the phone ability is off (Reach settings › Phones)");
                else
                {
                    try { r = await phones.RunAsync(a, ct); }
                    catch (ToolError e) { r = ToolResult.Error(e.Code, e.Hint); }
                }
            }
            else
            {
                r = await worker.CallAsync(name, a, def.TimeoutMs, ct);
                if (name == "device" && !r.IsError && self != null)
                    r = r with { Content = new[] { Content.Of(r.Content[0].Text + $"\nworker restarts {worker.Restarts}\n" + self.Tokens.Text()) } };
            }
            Audit.Call(name, args0, r, sw.ElapsedMilliseconds, how);
            Stats.Record(name, r.IsError);
            return r;
        }

        var flows = new FlowRunner((n, a, ct) => Exec(n, a, ct, "flow"));
        var handler = new McpHandler(
            Catalog.Tools,
            async (name, a, ct) =>
            {
                if (name == "flows")
                {
                    // Runs here in the server: it calls the worker step by step itself.
                    var sw = Stopwatch.StartNew();
                    var args0 = (System.Text.Json.Nodes.JsonObject)a.DeepClone();
                    ToolResult fr;
                    try { fr = Pause.IsOn ? ToolResult.Error("paused", Pause.Hint) : await flows.RunAsync(a, ct); }
                    catch (ToolError e) { fr = ToolResult.Error(e.Code, e.Hint); }
                    Audit.Call(name, args0, fr, sw.ElapsedMilliseconds, via);
                    Stats.Record(name, fr.IsError);
                    return fr;
                }
                var copy = (System.Text.Json.Nodes.JsonObject)a.DeepClone();
                var r = await Exec(name, a, ct, via);
                flows.Saw(name, copy, r);
                return r;
            },
            "reach-windows",
            $"{Version} (contract 0.1)",
            Catalog.Instructions,
            // Read on every tools/list: abilities switched on in settings show up when the agent reconnects.
            listed: t => Config.Current.Allows(t.Name));
        self = handler;

        // The kill switch also stops whatever is running right now.
        using var watch = new System.Threading.Timer(_ =>
        {
            if (Pause.IsOn && worker.Busy)
            {
                worker.Stop(new ToolError("paused", "the user paused Reach; the running action was stopped. " + Pause.Hint));
                Audit.Note("a running action was stopped (paused)");
            }
        }, null, 300, 300);
        Ui.Tray.Start(via, Stats.Text);

        if (has("--http"))
        {
            var i = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
            var port = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) ? p : 8766;
            var network = has("--lan") ? "lan" : has("--tailscale") ? "tailscale" : Config.Current.Network;
            var server = new HttpServer(handler, Token.Get, port, allNetworks: network is "lan" or "tailscale",
                allow: network == "tailscale" ? Net.LocalOrTailscale : null);
            server.Start();
            var where = network switch
            {
                "lan" => " (every network: anyone who can reach this PC and has the token)",
                "tailscale" => $" (and your Tailscale devices: http://{Net.TailscaleAddress() ?? "<tailscale address>"}:{server.BoundPort}/mcp)",
                _ => " (this PC only)",
            };
            Console.Error.WriteLine($"Reach for Windows {Version} on http://127.0.0.1:{server.BoundPort}/mcp{where} · token: reach.exe --token · Ctrl+C stops");
            var quit = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
            await quit.Task;
            server.Stop();
            return 0;
        }

        Console.Error.WriteLine($"Reach for Windows {Version} (stdio)");
        await StdioServer.RunAsync(handler);
        return 0;
    }

    private static int RunWindow(Func<Form> make)
    {
        var code = 0;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.Run(make());
            }
            catch (Exception e) { Console.Error.WriteLine(e); code = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>Hides the console only when it was made just for us (double-click), never a terminal the user typed in.</summary>
    private static void HideOwnConsole()
    {
        try
        {
            var h = GetConsoleWindow();
            if (h != IntPtr.Zero && GetConsoleProcessList(new uint[4], 4) <= 1) ShowWindow(h, 0);
        }
        catch { }
    }
}

/// <summary>Calls so far, for the tray's status line.</summary>
internal static class Stats
{
    private static int _calls;
    private static string? _last;
    private static DateTime _at;

    public static void Record(string tool, bool failed)
    {
        Interlocked.Increment(ref _calls);
        _last = tool + (failed ? " (error)" : "");
        _at = DateTime.Now;
    }

    public static string Text()
    {
        if (_calls == 0) return "running · no calls yet";
        var ago = DateTime.Now - _at;
        var when = ago.TotalSeconds < 60 ? $"{(int)ago.TotalSeconds} s ago" : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago" : _at.ToString("HH:mm");
        return $"{_calls} calls · last: {_last}, {when}";
    }
}

/// <summary>The HTTP token, made on this PC and kept in %LOCALAPPDATA%\Reach\token (only this user can read it).</summary>
public static class Token
{
    private static string PathOf => Path.Combine(Config.Dir, "token");

    public static string Get()
    {
        try
        {
            if (File.Exists(PathOf))
            {
                var t = File.ReadAllText(PathOf).Trim();
                if (t.Length >= 16) return t;
            }
        }
        catch { }
        return New();
    }

    public static string New()
    {
        var t = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, t);
        return t;
    }
}
