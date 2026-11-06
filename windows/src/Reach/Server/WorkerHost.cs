using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Reach.Mcp;

namespace Reach.Server;

/// <summary>
/// Runs the UI worker (`reach.exe --worker`) and talks to it one JSON line per request. All UI Automation work
/// happens there, so a frozen app can only freeze the worker: when a call runs past its time limit the worker is
/// killed and started again, and the server (and the app hosting the agent) never hangs.
/// </summary>
public sealed class WorkerHost(string[] workerArgs, string? exe = null) : IDisposable
{
    private readonly object _lock = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private long _nextId;
    public int Restarts { get; private set; }

    public int? Pid { get { lock (_lock) return _proc is { HasExited: false } p ? p.Id : null; } }

    private void EnsureStarted()
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false }) return;
            var psi = new ProcessStartInfo(exe ?? Environment.ProcessPath!, string.Join(' ', workerArgs.Prepend("--worker")))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            var p = Process.Start(psi) ?? throw new ToolError("internal", "could not start the UI worker");
            _proc = p;
            _stdin = p.StandardInput;
            _stdin.AutoFlush = true;
            _stdin.NewLine = "\n";
            _ = Task.Run(() => ReadLoop(p));
            _ = Task.Run(async () =>
            {
                // Worker logs pass through to our stderr, which MCP clients show as server logs.
                string? l;
                while ((l = await p.StandardError.ReadLineAsync()) != null) Console.Error.WriteLine("[worker] " + l);
            });
        }
    }

    private async Task ReadLoop(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (o?["id"] is JsonValue v && v.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(o);
            }
        }
        finally
        {
            // Worker gone: everything waiting on it fails now instead of at its timeout.
            foreach (var (id, tcs) in _pending)
                if (_pending.TryRemove(id, out _)) tcs.TrySetException(new ToolError("internal", "the UI worker stopped; it restarts on the next call"));
        }
    }

    public async Task<ToolResult> CallAsync(string tool, JsonObject args, int timeoutMs, CancellationToken ct)
    {
        EnsureStarted();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["tool"] = tool, ["args"] = args }.ToJsonString();
        lock (_lock) _stdin!.WriteLine(msg);

        // The worker gets a little less time than the MCP limit, so the agent hears why.
        var limit = Math.Max(1_000, timeoutMs - 1_500);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(limit, ct).ContinueWith(_ => { }));
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            Restart();
            return ToolResult.Error("timeout",
                $"{tool} got no answer in {limit / 1000} s (the window may be frozen); the UI worker was restarted. observe again, or pick another window");
        }
        JsonObject reply;
        try { reply = await tcs.Task; }
        catch (ToolError e) { return ToolResult.Error(e.Code, e.Hint); }
        if (reply["result"] is JsonObject r) return ToolResult.FromJson(r);
        var err = reply["error"] as JsonObject;
        return ToolResult.Error(err?.Str("code") ?? "internal", err?.Str("hint") ?? "worker error");
    }

    /// <summary>A call is running in the worker right now.</summary>
    public bool Busy => !_pending.IsEmpty;

    /// <summary>The kill switch: fails what is running with [reason] and kills the worker (a fresh one starts next call).</summary>
    public void Stop(ToolError reason)
    {
        foreach (var (id, tcs) in _pending)
            if (_pending.TryRemove(id, out _)) tcs.TrySetException(reason);
        Restart();
    }

    public void Restart()
    {
        lock (_lock)
        {
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            _proc = null;
            Restarts++;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _stdin?.Close(); } catch { }
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        }
    }
}
