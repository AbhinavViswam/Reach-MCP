using System.Reflection;
using System.Security.Cryptography;
using Reach.Mcp;
using Reach.Server;

namespace Reach;

/// <summary>
/// reach.exe
///   (no arguments)        MCP over stdio, for agents that start the server themselves (Claude Code, Cursor, …)
///   --http [--port N]     MCP over HTTP on 127.0.0.1:8766 with a token (--lan: every network; use with care)
///   --background-only     never use the real mouse/keyboard; reply needs_foreground instead
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
        if (has("--help") || has("-h"))
        {
            Console.WriteLine("reach.exe [--http [--port N] [--lan]] [--background-only] | --token [--new] | --selftest");
            return 0;
        }

        var config = Core.Config.Load();
        using var worker = new WorkerHost(backgroundOnly ? new[] { "--background-only" } : Array.Empty<string>());
        McpHandler? self = null;
        var handler = new McpHandler(
            Catalog.Tools.Where(t => config.Allows(t.Name)).ToArray(),
            async (name, a, ct) =>
            {
                var def = Catalog.Find(name)!;
                var r = await worker.CallAsync(name, a, def.TimeoutMs, ct);
                if (name == "device" && !r.IsError && self != null)
                    r = r with { Content = new[] { Content.Of(r.Content[0].Text + $"\nworker restarts {worker.Restarts}\n" + self.Tokens.Text()) } };
                return r;
            },
            "reach-windows",
            $"{Version} (contract 0.1)",
            Catalog.Instructions);
        self = handler;

        if (has("--http"))
        {
            var i = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
            var port = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) ? p : 8766;
            var server = new HttpServer(handler, Token.Get, port, allNetworks: has("--lan"));
            server.Start();
            Console.Error.WriteLine($"Reach for Windows {Version} on http://127.0.0.1:{server.BoundPort}/mcp" +
                                    (has("--lan") ? " (all networks)" : "") + " · token: reach.exe --token · Ctrl+C stops");
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
}

/// <summary>The HTTP token, made on this PC and kept in %LOCALAPPDATA%\Reach\token (only this user can read it).</summary>
public static class Token
{
    private static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reach", "token");

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
