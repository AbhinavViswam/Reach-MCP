using Reach.Mcp;

namespace Reach.Core;

/// <summary>One line of text found on a screenshot, in screen pixels.</summary>
public sealed record OcrLine(string Text, Box Bounds);

/// <summary>
/// Seeing what UI Automation can't: OCR text becomes ordinary elements with IDs. The same rules as Reach for
/// Android (android/.../screen/Vision.kt).
/// </summary>
public static class Vision
{
    public const string OcrClass = "ocr";

    /// <summary>Fewer than 3 elements with words below [ignoreAboveY] (the title bar): read with OCR unasked.</summary>
    public static bool Thin(IEnumerable<Element> elements, int ignoreAboveY = int.MinValue) =>
        elements.Count(e => e.Label.Trim().Length > 0 && e.Bounds.Top >= ignoreAboveY && e.Raw.ClassName != OcrClass) < 3;

    /// <summary>OCR lines as elements, minus what the tree already says and stray marks.</summary>
    public static List<RawElement> Extra(IReadOnlyList<Element> tree, IEnumerable<OcrLine> lines)
    {
        var o = new List<RawElement>();
        foreach (var l in lines)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(l.Text, @"\s+", " ").Trim();
            if (text.Count(char.IsLetterOrDigit) < 2) continue;
            var n = Ids.Normalize(text);
            var known = tree.Any(e => e.Bounds.Contains(l.Bounds.Cx, l.Bounds.Cy) &&
                                      (Ids.Normalize(e.Label).Contains(n) || Ids.Normalize(e.Value ?? "").Contains(n)));
            if (known) continue;
            o.Add(new RawElement(Role.Text, text.Length > 120 ? text[..120] : text, null, null, OcrClass, l.Bounds));
        }
        return o;
    }

    /// <summary>Elements worth a numbered box; boxes over 40% of [area] (whole panes) are left out.</summary>
    public static List<Element> Markable(IEnumerable<Element> elements, long area, int max = 80) =>
        elements.Where(e => !e.Bounds.IsEmpty && e.Bounds.Area * 10 <= area * 4 && (e.Role.Pressable() || e.Label.Length > 0)).Take(max).ToList();
}

/// <summary>
/// A rough count of the tokens each tool's replies cost: text at ~4 characters a token, images at
/// width × height / 750 (how Claude counts them). For comparing modes and servers, not a bill.
/// </summary>
public sealed class Tokens
{
    private sealed class Row { public int Calls; public long Tokens; public int Images; }
    private readonly Dictionary<string, Row> _rows = new();
    private readonly List<string> _order = new();

    public static long Estimate(string text) => (text.Length + 3) / 4;

    public void Add(string tool, ToolResult r)
    {
        lock (_rows)
        {
            if (!_rows.TryGetValue(tool, out var row)) { row = new Row(); _rows[tool] = row; _order.Add(tool); }
            row.Calls++;
            foreach (var c in r.Content)
            {
                if (c.Type == "image") { row.Images++; row.Tokens += c.Width > 0 ? (long)c.Width * c.Height / 750 : 1_600; }
                else row.Tokens += Estimate(c.Text ?? "");
            }
        }
    }

    public string Text()
    {
        lock (_rows)
        {
            if (_order.Count == 0) return "replies so far: none";
            return "replies so far (≈tokens per call): " + string.Join(" · ", _order.Select(n =>
            {
                var r = _rows[n];
                return $"{n} {r.Calls}× ~{r.Tokens / r.Calls}" + (r.Images > 0 ? $" ({r.Images} images)" : "");
            }));
        }
    }
}
