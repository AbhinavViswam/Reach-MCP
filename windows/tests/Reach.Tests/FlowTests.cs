using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;
using Reach.Server;

namespace Reach.Tests;

[Collection("reach-dir")]
public class FlowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reach-flow").FullName;

    public FlowTests() => Config.DirOverride = _dir;

    public void Dispose()
    {
        Config.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    [Fact]
    public void RecordedTapsKeepTheirWords()
    {
        var steps = Flow.StepsOf("act", J("""{"action":"tap","target":"e4k2"}"""), ToolResult.Text("verdict: done · click e4k2 \"Send\" (invoked in the background)\nchanges v1 → v2"));
        Assert.Single(steps);
        Assert.Equal("Send", (string?)steps[0]["args"]!["target"]!["text"]);
        Assert.Equal("e4k2", (string?)steps[0]["args"]!["target"]!["id"]);
        Assert.Equal("done", (string?)steps[0]["verdict"]);
        Assert.Empty(Flow.StepsOf("observe", new JsonObject(), ToolResult.Text("app x")));
        Assert.Empty(Flow.StepsOf("act", new JsonObject(), ToolResult.Error("stale_id", "gone")));

        var batch = Flow.StepsOf("act_batch",
            J("""{"actions":[{"action":"tap","target":"e1aa"},{"action":"type","target":"e2bb","text":"hi"},{"action":"key","key":"enter"}]}"""),
            ToolResult.Text("verdict: no_effect · 2 of 3\nstep 1: done · click e1aa \"Search\"\nstep 2: no_effect · type into e2bb \"Box\"\nstopped"));
        Assert.Equal(2, batch.Count);
        Assert.Equal("no_effect", (string?)batch[1]["verdict"]);
        Assert.Equal("Box", (string?)batch[1]["args"]!["target"]!["text"]);
    }

    [Fact]
    public void InputsAreFilledIn()
    {
        var steps = new JsonArray
        {
            J("""{"tool":"apps","args":{"action":"open","name":"notepad"}}"""),
            J("""{"tool":"act","args":{"action":"type","target":{"id":"e9zz","text":"Text editor"},"text":"Dear Bob, hello"}}"""),
        };
        var inputs = Flow.MakeInputs(steps, J("""{"who":"Bob","missing":"nowhere"}"""));
        Assert.Equal("Bob", (string?)inputs["who"]);
        Assert.Null(inputs["missing"]);
        Assert.Equal("Dear {{who}}, hello", (string?)steps[1]!["args"]!["text"]);
        var filled = Flow.Fill((JsonObject)steps[1]!["args"]!, inputs, J("""{"who":"Ann \"A\" Lee"}"""));
        Assert.Equal("Dear Ann \"A\" Lee, hello", (string?)filled["text"]);
        Assert.Equal("Dear Bob, hello", (string?)Flow.Fill((JsonObject)steps[1]!["args"]!, inputs, null)["text"]);
        Assert.Throws<ToolError>(() => Flow.Fill(J("""{"text":"{{nope}}"}"""), new JsonObject(), null));
    }

    [Fact]
    public void GoodMeansAsRecorded()
    {
        var done = J("""{"verdict":"done"}""");
        Assert.True(Flow.Good(done, ToolResult.Text("verdict: done · x")));
        Assert.True(Flow.Good(done, ToolResult.Text("opened Notepad")));
        Assert.False(Flow.Good(done, ToolResult.Text("verdict: no_effect · x")));
        Assert.True(Flow.Good(J("""{"verdict":"no_effect"}"""), ToolResult.Text("verdict: no_effect · x")));
        Assert.False(Flow.Good(done, ToolResult.Text("verdict: blocked_by_popup · x")));
        Assert.False(Flow.Good(done, ToolResult.Error("stale_id", "x")));
        Assert.Throws<ToolError>(() => Flow.CleanName("  "));
        Assert.Equal("weekly_report", Flow.CleanName("Weekly report"));
    }

    [Fact]
    public async Task RecordReplayAndHeal()
    {
        // A pretend PC: the Send button's id changes after the recording.
        var calls = new List<string>();
        var sendId = "e1aa";
        Task<ToolResult> Device(string tool, JsonObject a, CancellationToken _)
        {
            calls.Add(tool + " " + a.ToJsonString());
            if (tool == "observe") return Task.FromResult(ToolResult.Text("app notepad\ne3cc button \"Send\""));
            if (tool == "apps") return Task.FromResult(ToolResult.Text("opened Mail"));
            if (a["target"] is JsonValue v && v.GetValue<string>() != sendId) return Task.FromResult(ToolResult.Error("stale_id", "gone"));
            if (a["target"] is JsonObject && sendId == "zzzz" && a.Str("action") == "tap") return Task.FromResult(ToolResult.Error("not_found", "no Send"));
            if (a["target"] is JsonObject { } t && (string?)t["text"] == "Send") return Task.FromResult(ToolResult.Text($"verdict: done · click {sendId} \"Send\""));
            if (a.Str("action") == "type") return Task.FromResult(ToolResult.Text($"verdict: done · type into e2bb \"To\" \"{a.Str("text")}\""));
            return Task.FromResult(ToolResult.Text($"verdict: done · click {sendId} \"Send\""));
        }
        var runner = new FlowRunner(Device);
        await runner.RunAsync(J("""{"action":"record_start","name":"Mail Bob"}"""), default);
        foreach (var (tool, args) in new[]
                 {
                     ("apps", J("""{"action":"open","name":"mail"}""")),
                     ("observe", new JsonObject()),
                     ("act", J("""{"action":"type","target":{"text":"To"},"text":"bob@x.com"}""")),
                     ("act", J("""{"action":"tap","target":"e1aa"}""")),
                 })
            runner.Saw(tool, args, await Device(tool, args, default));
        var stop = await runner.RunAsync(J("""{"action":"record_stop","inputs":{"to":"bob@x.com"}}"""), default);
        Assert.Contains("3 steps", stop.Content[0].Text);
        Assert.Contains("inputs to", stop.Content[0].Text);

        sendId = "e7ff"; // the app changed
        calls.Clear();
        var run = await runner.RunAsync(J("""{"action":"run","name":"mail_bob","inputs":{"to":"ann@y.org"}}"""), default);
        Assert.False(run.IsError);
        Assert.StartsWith("verdict: done · flow \"mail_bob\" ran 3 steps", run.Content[0].Text);
        Assert.Contains("found again by \"Send\"", run.Content[0].Text);
        Assert.Contains(calls, c => c.Contains("ann@y.org"));
        var saved = File.ReadAllText(Path.Combine(FlowRunner.Dir, "mail_bob.json"));
        Assert.Contains("e7ff", saved); // the new id was kept

        // A step that fails stops the run and hands back the screen.
        sendId = "zzzz";
        var broken = await runner.RunAsync(J("""{"action":"run","name":"mail_bob","inputs":{"to":"a@b"}}"""), default);
        Assert.Contains("stopped at step 3/3", broken.Content[0].Text);
        Assert.Contains("from_step=4", broken.Content[0].Text);
        Assert.Contains("e3cc button", broken.Content[0].Text);
    }

    [Fact]
    public void UndoPutsFilesBack()
    {
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        var f = Path.Combine(work, "a.txt");
        File.WriteAllText(f, "first");
        var backup = Journal.Backup(f);
        File.WriteAllText(f, "second");
        Deep.Undo.FileWritten(f, existed: true, backup);

        var g = Path.Combine(work, "b.txt");
        File.WriteAllText(g, "keep me");
        var gb = Journal.Backup(g);
        File.Delete(g);
        Deep.Undo.FileDeleted(g, gb, folder: false);

        var cp = Deep.Undo.Run(J("""{"action":"checkpoint","name":"before move"}"""));
        Assert.Contains("checkpoint", cp.Content[0].Text);
        var moved = Path.Combine(work, "c.txt");
        File.Move(f, moved);
        Deep.Undo.FileMoved(f, moved);
        Deep.Undo.CantUndo("sent mail to bob");

        var list = Deep.Undo.Run(new JsonObject()).Content[0].Text;
        Assert.Contains("can't undo", list);
        Assert.Contains("can undo", list);

        // Back to the checkpoint: only the move is undone.
        var to = Deep.Undo.Run(J("""{"action":"to","target":"before move"}"""));
        Assert.StartsWith("verdict: done · undid 1 changes", to.Content[0].Text);
        Assert.True(File.Exists(f));
        Assert.Equal("second", File.ReadAllText(f));

        // Then the rest, one at a time, newest first.
        Assert.Contains(g, Deep.Undo.Run(J("""{"action":"last"}""")).Content[0].Text);
        Assert.Equal("keep me", File.ReadAllText(g));
        Deep.Undo.Run(J("""{"action":"last"}"""));
        Assert.Equal("first", File.ReadAllText(f));
        Assert.Equal("nothing", Assert.Throws<ToolError>(() => Deep.Undo.Run(J("""{"action":"last"}"""))).Code);
    }
}
