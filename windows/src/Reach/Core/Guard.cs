using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reach.Mcp;

namespace Reach.Core;

/// <summary>
/// Hides one-time codes and card numbers before text leaves the PC (spec/POLICY.md "Masking"). The same rules as
/// Reach for Android's Mask.kt, with the same tests.
/// </summary>
public static partial class Mask
{
    [GeneratedRegex(@"(?i)\b(otp|one[ -]?time|passcode|pass code|verification|verify|security code|login code|auth(entication)? code|2fa|pin|code)\b|ओटीपी|कोड")]
    private static partial Regex OtpWords();

    [GeneratedRegex(@"(?<!\d)(?<!\d[.,])\d{4,8}(?!\d)(?![.,]\d)")]
    private static partial Regex ShortNumber();

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CardLike();

    public const string Note = "(codes and card numbers hidden; reveal=true asks the user on this PC)";

    public static (string Text, bool Masked) Apply(string text)
    {
        var masked = false;
        var o = CardLike().Replace(text, m =>
        {
            var d = new string(m.Value.Where(char.IsDigit).ToArray());
            if (d.Length is >= 13 and <= 19 && Luhn(d)) { masked = true; return "•••• " + d[^4..]; }
            return m.Value;
        });
        if (OtpWords().IsMatch(o))
        {
            o = ShortNumber().Replace(o, m =>
            {
                var v = m.Value;
                if (v.Length == 4 && (v.StartsWith("19") || v.StartsWith("20"))) return v; // years
                masked = true;
                return new string('•', v.Length);
            });
        }
        return (o, masked);
    }

    public static bool Luhn(string digits)
    {
        int sum = 0;
        var dbl = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}

/// <summary>Which paths the files tool may touch: only the folders in config (spec/POLICY.md). Tested.</summary>
public static class Fence
{
    /// <summary>[path] as a full path inside one of [allowed] (full folder paths), else a tool error.</summary>
    public static string Resolve(IReadOnlyList<string> allowed, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolError("bad_args", "give a path, e.g. Documents\\notes.txt");
        var p = path.Trim().Trim('"').Replace('/', '\\');
        string full;
        if (Path.IsPathRooted(p)) full = Path.GetFullPath(p);
        else
        {
            // "Documents\a.txt": the first part names an allowed folder.
            var first = p.Split('\\')[0];
            var root = allowed.FirstOrDefault(a => string.Equals(Path.GetFileName(a.TrimEnd('\\')), first, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ToolError("blocked", $"Reach only works in {string.Join(", ", allowed.Select(a => Path.GetFileName(a.TrimEnd('\\'))))}");
            full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(root.TrimEnd('\\'))!, p));
        }
        foreach (var a in allowed)
        {
            var dir = Path.GetFullPath(a).TrimEnd('\\');
            if (string.Equals(full, dir, StringComparison.OrdinalIgnoreCase) || full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return full;
        }
        throw new ToolError("blocked", $"Reach only works in {string.Join(", ", allowed)} (change folders in config.json)");
    }
}
