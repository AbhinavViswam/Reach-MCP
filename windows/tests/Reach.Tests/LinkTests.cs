using System.Net;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;
using Reach.Server;

namespace Reach.Tests;

[Collection("reach-dir")]
public class LinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reach-link").FullName;

    public LinkTests() => Config.DirOverride = _dir;

    public void Dispose()
    {
        Config.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void TailscaleAddresses()
    {
        Assert.True(Net.LocalOrTailscale(IPAddress.Loopback));
        Assert.True(Net.LocalOrTailscale(IPAddress.Parse("100.101.7.3")));
        Assert.True(Net.LocalOrTailscale(IPAddress.Parse("100.127.255.1")));
        Assert.False(Net.LocalOrTailscale(IPAddress.Parse("100.128.0.1")));
        Assert.False(Net.LocalOrTailscale(IPAddress.Parse("192.168.1.20")));
        Assert.True(Net.LocalOrTailscale(IPAddress.Parse("fd7a:115c:a1e0::1")));
        Assert.False(Net.LocalOrTailscale(IPAddress.Parse("fd00::1")));
        Assert.True(Net.LocalOrTailscale(IPAddress.Parse("::ffff:100.64.0.9")));
    }

    [Fact]
    public void OldPhoneSettingsMoveIntoPhones()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Config.PathOf, """{"phoneApprovals":true,"phoneUrl":"http://10.0.0.5:8765","phoneToken":"abc123"}""");
        var c = Config.Load();
        Assert.Single(c.Phones);
        Assert.Equal("http://10.0.0.5:8765", c.Phones[0].Url);
        Assert.Equal("abc123", c.ApprovalLink()!.Token);
        Assert.True(c.Allows("phone"));
        c.Save();
        Assert.DoesNotContain("phoneToken", File.ReadAllText(Config.PathOf));
        Assert.False(new Config().Allows("phone")); // no phone linked: no phone tool
    }

    private static readonly ToolDef[] PhoneTools =
    {
        new("observe", "Reads the phone screen. More text.", new JsonObject { ["type"] = "object" }),
        new("act", "Taps.", new JsonObject { ["type"] = "object" }),
    };

    [Fact]
    public async Task PcUsesThePhone()
    {
        // A pretend phone: Reach's own MCP handler and HTTP server with two tools.
        var phone = new McpHandler(PhoneTools, (name, a, _) => Task.FromResult(name == "observe"
            ? ToolResult.Text("app com.whatsapp · v3\ne1aa text \"Your OTP is ••••••\"")
            : ToolResult.Text($"verdict: done · tap {a.Str("target")} \"Send\"\nchanges v3 → v4")), "reach-android", "0", "");
        var server = new HttpServer(phone, () => "phone-secret", 0, allNetworks: false);
        server.Start();
        try
        {
            var c = new Config();
            c.Phones.Add(new Config.PhoneLink { Name = "pixel", Url = $"http://127.0.0.1:{server.BoundPort}", Token = "phone-secret" });
            c.Save();
            var hub = new PhoneHub();

            var tools = await hub.RunAsync(new JsonObject { ["tool"] = "tools" }, default);
            Assert.Contains("observe: Reads the phone screen.", tools.Content[0].Text);
            Assert.DoesNotContain("More text", tools.Content[0].Text);

            var act = await hub.RunAsync(new JsonObject { ["tool"] = "act", ["args"] = new JsonObject { ["action"] = "tap", ["target"] = "e1aa" } }, default);
            Assert.False(act.IsError);
            Assert.StartsWith("verdict: done · tap e1aa \"Send\" · on phone pixel\nchanges", act.Content[0].Text);

            // A flow step through the phone keeps the element's words.
            var step = Flow.StepsOf("phone", new JsonObject { ["tool"] = "act", ["args"] = new JsonObject { ["action"] = "tap", ["target"] = "e1aa" } }, act);
            Assert.Equal("Send", (string?)step[0]["args"]!["args"]!["target"]!["text"]);
            Assert.Empty(Flow.StepsOf("phone", new JsonObject { ["tool"] = "observe" }, act));

            c.Phones[0].Token = "wrong";
            c.Save();
            var bad = await Assert.ThrowsAsync<ToolError>(() => new PhoneHub().RunAsync(new JsonObject { ["tool"] = "observe" }, default));
            Assert.Equal("bad_token", bad.Code);

            c.Phones.Add(new Config.PhoneLink { Name = "tablet", Url = "http://127.0.0.1:1", Token = "x" });
            c.Save();
            Assert.Equal("bad_args", (await Assert.ThrowsAsync<ToolError>(() => hub.RunAsync(new JsonObject { ["tool"] = "observe" }, default))).Code);
            Assert.Equal("unreachable", (await Assert.ThrowsAsync<ToolError>(() => hub.RunAsync(new JsonObject { ["tool"] = "observe", ["device"] = "tablet" }, default))).Code);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task TailscaleModeTurnsOthersAway()
    {
        var h = new McpHandler(PhoneTools, (_, _, _) => Task.FromResult(ToolResult.Text("ok")), "t", "0", "");
        var server = new HttpServer(h, () => "s", 0, allNetworks: false, allow: _ => false);
        server.Start();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAnyAsync<Exception>(() => http.GetStringAsync($"http://127.0.0.1:{server.BoundPort}/health"));
        }
        finally { server.Stop(); }
    }
}
