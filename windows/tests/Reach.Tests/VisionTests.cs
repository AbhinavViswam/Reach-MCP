using Reach.Core;
using Reach.Mcp;

namespace Reach.Tests;

public class VisionTests
{
    private static RawElement El(Role r, string label, Box b) => new(r, label, null, null, "Ctl", b);

    [Fact]
    public void ThinWindowsGetOcr()
    {
        // An Electron window before Chromium turns accessibility on: only the title bar's buttons.
        var titleBarOnly = Ids.Assign("code", new[]
        {
            El(Role.Button, "Minimize", new Box(1700, 0, 1750, 30)),
            El(Role.Button, "Restore", new Box(1750, 0, 1800, 30)),
            El(Role.Button, "Close", new Box(1800, 0, 1850, 30)),
        });
        Assert.True(Vision.Thin(titleBarOnly, ignoreAboveY: 48));
        Assert.False(Vision.Thin(titleBarOnly));
    }

    [Fact]
    public void OcrSkipsKnownTextAndStrayMarks()
    {
        var tree = Ids.Assign("x", new[] { El(Role.Button, "Save changes", new Box(0, 0, 300, 50)) });
        var extra = Vision.Extra(tree, new[]
        {
            new OcrLine("Save changes", new Box(10, 10, 200, 40)),
            new OcrLine("Level 3", new Box(400, 400, 600, 450)),
            new OcrLine("|", new Box(0, 500, 5, 520)),
        });
        Assert.Equal(new[] { "Level 3" }, extra.Select(e => e.Label));
        Assert.Contains("(ocr)", ViewText.Line(Ids.Assign("x", extra)[0]));
    }

    [Fact]
    public void TokenEstimate()
    {
        var t = new Tokens();
        t.Add("observe", ToolResult.Text(new string('x', 400)));
        t.Add("observe", new ToolResult(new[] { Content.Image("", "image/jpeg", 750, 1000) }));
        Assert.Contains("observe 2× ~", t.Text());
        Assert.Contains("(1 images)", t.Text());
    }
}
