using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Worker;

/// <summary>
/// The UI worker: reads one JSON request per line on stdin, answers one per line on stdout, one at a time. The
/// server kills and restarts it when a call hangs (Server/WorkerHost.cs). stdout carries only answers; logs go to stderr.
/// </summary>
public static class WorkerMain
{
    public static int Run(bool backgroundOnly)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Uia.StartEvents();
        var tools = new Tools(backgroundOnly);
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            JsonObject? req;
            try { req = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            if (req == null) continue;
            var id = req["id"]?.DeepClone();
            var tool = req.Str("tool") ?? "";
            var args = req["args"] as JsonObject ?? new JsonObject();
            JsonObject reply;
            try
            {
                reply = new JsonObject { ["id"] = id, ["result"] = tools.Run(tool, args).ToJson(sizes: true) };
            }
            catch (ToolError e)
            {
                reply = new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = e.Code, ["hint"] = e.Hint } };
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                reply = new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = "internal", ["hint"] = e.Message } };
            }
            stdout.WriteLine(reply.ToJsonString());
        }
        return 0;
    }

    /// <summary>Reads the front window once and prints it. Changes nothing.</summary>
    public static int SelfTest()
    {
        var sw = Stopwatch.StartNew();
        var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
        var live = Uia.Read(fg);
        var snap = new ScreenState().Store(live.Snap);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(ViewText.Full(snap));
        Console.WriteLine($"-- {snap.Elements.Count} elements in {sw.ElapsedMilliseconds} ms");
        foreach (var m in Desktop.Monitors()) Console.WriteLine("-- " + m);
        Console.WriteLine($"-- {Desktop.Windows().Count} open windows");
        return 0;
    }
}

/// <summary>
/// Installed apps, as the Start menu lists them (Win32 and Store apps alike), opened through shell:AppsFolder.
/// </summary>
internal static class StartApps
{
    public sealed record App(string Name, string AppId);

    private static List<App>? _cache;
    private static DateTime _at;

    public static List<App> All()
    {
        if (_cache != null && DateTime.UtcNow - _at < TimeSpan.FromMinutes(5)) return _cache;
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var list = new List<App>();
        try
        {
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var node = JsonNode.Parse(json);
            var arr = node as JsonArray ?? new JsonArray(node?.DeepClone());
            foreach (var n in arr)
                if (n is JsonObject o && o.Str("Name") is string name && o.Str("AppID") is string id)
                    list.Add(new App(name, id));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Get-StartApps failed: " + e.Message);
        }
        _cache = list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _at = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>Starts the app; returns the name it was started as.</summary>
    public static string Launch(string name)
    {
        var n = Ids.Normalize(name);
        var apps = All();
        var app = apps.FirstOrDefault(a => Ids.Normalize(a.Name) == n)
                  ?? apps.FirstOrDefault(a => Ids.Normalize(a.Name).StartsWith(n))
                  ?? apps.FirstOrDefault(a => Ids.Normalize(a.Name).Contains(n));
        if (app != null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppId}") { UseShellExecute = true });
            return app.Name;
        }
        // Not in the Start menu: an exe on the PATH or in App Paths ("notepad", "calc", a full path).
        try
        {
            Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
            return name;
        }
        catch
        {
            var close = apps.OrderByDescending(a => Ids.Similarity(Ids.Normalize(a.Name), n)).Take(5).Select(a => a.Name);
            throw new ToolError("not_found", $"no app called \"{name}\"; closest: {string.Join(", ", close)}");
        }
    }
}
