using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Tests;

// One temp Reach folder for these tests; they share Config.Dir, so they must not run in parallel with each other.
[Collection("reach-dir")]
public class SafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reach-safety").FullName;

    public SafetyTests() => Config.DirOverride = _dir;

    public void Dispose()
    {
        Config.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void AppRulesUserBeatsBuiltin()
    {
        var none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(AppRule.Block, Privacy.Rule("KeePass", "Database - KeePass", none));
        Assert.Equal(AppRule.Block, Privacy.Rule("msedge", "HDFC Bank NetBanking - Edge", none));
        Assert.Equal(AppRule.Allow, Privacy.Rule("notepad", "notes.txt - Notepad", none));

        var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["keepass"] = "allow",          // the user's own choice wins over the built-in block
            ["excel"] = "read_only",
            ["hdfc bank"] = "read only",
            ["secret project"] = "block",
            ["ab"] = "block",               // too short to match titles
        };
        Assert.Equal(AppRule.Allow, Privacy.Rule("keepass", "Database - KeePass", user));
        Assert.Equal(AppRule.ReadOnly, Privacy.Rule("EXCEL", "Budget.xlsx - Excel", user));
        Assert.Equal(AppRule.ReadOnly, Privacy.Rule("msedge", "HDFC Bank NetBanking - Edge", user));
        Assert.Equal(AppRule.Block, Privacy.Rule("code", "Secret Project - Visual Studio Code", user));
        Assert.Equal(AppRule.Allow, Privacy.Rule("notepad", "lab notes", user));
    }

    [Fact]
    public void ReadOnlyAppsRefuseActions()
    {
        var c = new Config();
        c.Apps["notepad"] = "read_only";
        c.Save();
        var e = Assert.Throws<ToolError>(() => Privacy.RequireCanAct("notepad", "x - Notepad", "notepad"));
        Assert.Equal("read_only_app", e.Code);
        c.Apps["notepad"] = "block";
        c.Save();
        Assert.Equal("blocked_app", Assert.Throws<ToolError>(() => Privacy.RequireCanAct("notepad", null, "notepad")).Code);
        c.Apps.Remove("notepad");
        c.Save();
        Privacy.RequireCanAct("notepad", null, "notepad");
    }

    [Fact]
    public void ConfigSavesAndReloads()
    {
        var c = Config.Load(); // first run writes the defaults
        Assert.True(File.Exists(Config.PathOf));
        Assert.False(c.Shell);
        c.Set("shell", true);
        c.Apps["Word"] = "block";
        c.Save();
        var again = Config.Load();
        Assert.True(again.Shell);
        Assert.Equal("block", again.Apps["word"]); // rule names ignore case
        Assert.True(Config.Current.Allows("shell"));
    }

    [Fact]
    public void PauseFlag()
    {
        Assert.False(Pause.IsOn);
        Pause.Set(true);
        Assert.True(Pause.IsOn);
        Assert.Equal("paused", Assert.Throws<ToolError>(Pause.ThrowIfOn).Code);
        Pause.Set(false);
        Assert.False(Pause.IsOn);
        Pause.ThrowIfOn();
    }

    [Fact]
    public void AuditKeepsCallsAndHidesCodes()
    {
        Audit.Call("act", new JsonObject { ["action"] = "type", ["text"] = "my OTP is 482913" }, ToolResult.Text("verdict: done · type\nmore"), 42, "stdio");
        Audit.Call("files", new JsonObject(), ToolResult.Error("blocked", "nope"), 3, "stdio");
        Audit.Approval("send an email to a@b.c", "Subject: hi", false, "this PC");
        var rows = Audit.Read();
        Assert.Equal(3, rows.Count);
        Assert.Equal("approval", rows[0].Kind); // newest first
        Assert.False(rows[0].Ok);
        Assert.False(rows[1].Ok);
        Assert.Equal("act", rows[2].Tool);
        Assert.DoesNotContain("482913", rows[2].Args);
        Assert.Equal("verdict: done · type", rows[2].Result);
        Assert.Equal(42, rows[2].Ms);
        Audit.Clear();
        Assert.Empty(Audit.Read());
    }

    [Fact]
    public async Task ListedFollowsTheSwitches()
    {
        var on = false;
        var h = new McpHandler(Catalog.Tools, (_, _, _) => Task.FromResult(ToolResult.Text("ok")), "t", "0", "",
            listed: t => t.Name != "shell" || on);
        async Task<List<string>> Names() =>
            JsonNode.Parse((await h.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))!)!["result"]!["tools"]!
                .AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.DoesNotContain("shell", await Names());
        on = true;
        Assert.Contains("shell", await Names());
    }
}
