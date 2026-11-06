using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Deep;
using Reach.Mcp;

namespace Reach.Tests;

public class DeepTests
{
    // The same cases as android/.../DeepTest.kt: both platforms hide the same things.
    [Fact]
    public void MaskMatchesAndroid()
    {
        var otp = Mask.Apply("Your OTP is 482913. Do not share it.");
        Assert.True(otp.Masked);
        Assert.DoesNotContain("482913", otp.Text);
        var plain = Mask.Apply("Meeting at 1530 in room 4021, see you in 2026");
        Assert.False(plain.Masked);
        Assert.Contains("2026", Mask.Apply("Dress code for 2026 party").Text);
        var card = Mask.Apply("Card 4111 1111 1111 1111 was charged");
        Assert.Contains("•••• 1111", card.Text);
        Assert.False(Mask.Apply("Order 1234 5678 9012 3456").Masked);
        Assert.True(Mask.Apply("आपका ओटीपी 7788 है").Masked);
    }

    [Fact]
    public void FilesStayInAllowedFolders()
    {
        var root = Directory.CreateTempSubdirectory("reach").FullName;
        var docs = Path.Combine(root, "Documents");
        Directory.CreateDirectory(docs);
        var allowed = new[] { docs };
        Assert.Equal(Path.Combine(docs, "a.txt"), Fence.Resolve(allowed, "Documents\\a.txt"));
        Assert.Equal(Path.Combine(docs, "a.txt"), Fence.Resolve(allowed, "Documents/a.txt"));
        Assert.Equal(Path.Combine(docs, "a.txt"), Fence.Resolve(allowed, Path.Combine(docs, "a.txt")));
        Assert.Throws<ToolError>(() => Fence.Resolve(allowed, "Documents\\..\\secret.txt"));
        Assert.Throws<ToolError>(() => Fence.Resolve(allowed, "C:\\Windows\\System32\\drivers\\etc\\hosts"));
        Assert.Throws<ToolError>(() => Fence.Resolve(allowed, docs + "Evil\\x.txt"));
        Assert.Throws<ToolError>(() => Fence.Resolve(allowed, "AppData\\x"));
    }

    [Fact]
    public void SnapPlaces()
    {
        var work = new Box(0, 0, 1920, 1040);
        Assert.Equal(new Box(0, 0, 960, 1040), Snap.Place("left", work));
        Assert.Equal(new Box(960, 520, 1920, 1040), Snap.Place("bottom_right", work));
        // A second monitor to the left, at negative coordinates.
        Assert.Equal(new Box(-1280, 0, -640, 1000), Snap.Place("left", new Box(-1280, 0, 0, 1000)));
        Assert.Throws<ToolError>(() => Snap.Place("sideways", work));
    }

    [Fact]
    public void PageElementsGetStableIds()
    {
        JsonArray Items(int y, string value) => new()
        {
            new JsonObject { ["i"] = 0, ["tag"] = "input", ["type"] = "email", ["label"] = "Email", ["value"] = value, ["id"] = "email", ["x"] = 10, ["y"] = y, ["w"] = 200, ["h"] = 30 },
            new JsonObject { ["i"] = 1, ["tag"] = "input", ["type"] = "password", ["label"] = "Password", ["value"] = "hunter2", ["x"] = 10, ["y"] = y + 40, ["w"] = 200, ["h"] = 30 },
            new JsonObject { ["i"] = 2, ["tag"] = "button", ["label"] = "Sign in", ["id"] = "radix-:r3:", ["x"] = 10, ["y"] = y + 80, ["w"] = 90, ["h"] = 30 },
            new JsonObject { ["i"] = 3, ["tag"] = "div", ["label"] = "just a box", ["x"] = 0, ["y"] = 0, ["w"] = 9, ["h"] = 9 },
        };
        var a = PageModel.Elements("example.com", Items(100, ""));
        var b = PageModel.Elements("example.com", Items(400, "me@x.com"));
        Assert.Equal(3, a.Count); // the plain div is not an element
        Assert.Equal(a.Select(x => x.Element.Id), b.Select(x => x.Element.Id));
        Assert.Equal(Role.Field, a[0].Element.Role);
        Assert.Equal(Role.Password, a[1].Element.Role);
        Assert.Null(a[1].Element.Value); // never the password
        Assert.Null(a[2].Element.Raw.ResourceId); // framework-made id dropped
        Assert.Equal(2, a[2].Index);
    }

    [Fact]
    public void ShellIsOffByDefault()
    {
        var c = new Config();
        Assert.False(c.Allows("shell"));
        Assert.True(c.Allows("office"));
        Assert.True(c.Allows("observe"));
        var listed = Catalog.Tools.Where(t => c.Allows(t.Name)).Select(t => t.Name).ToList();
        Assert.DoesNotContain("shell", listed);
        Assert.Contains("browser", listed);
    }
}
