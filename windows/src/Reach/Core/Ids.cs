using System.Text.RegularExpressions;

namespace Reach.Core;

/// <summary>
/// Stable element IDs (spec/CONTRACT.md §2), identical to Reach for Android's: a hash of app + role + resource id
/// (UIA AutomationId) + class + label. Value and position are left out, so typing or scrolling keeps every ID.
/// </summary>
public static partial class Ids
{
    public static List<Element> Assign(string? app, IEnumerable<RawElement> raws)
    {
        var seen = new Dictionary<string, int>();
        var list = new List<Element>();
        foreach (var r in raws)
        {
            var b = "e" + Hash(Key(app, r));
            var n = seen.GetValueOrDefault(b) + 1;
            seen[b] = n;
            list.Add(new Element(n == 1 ? b : $"{b}-{n}", r));
        }
        return list;
    }

    internal static string Key(string? app, RawElement r)
    {
        var cls = r.ClassName;
        var dot = cls.LastIndexOf('.');
        if (dot >= 0) cls = cls[(dot + 1)..];
        return string.Join('\u0001', app ?? "", r.Role.Word(), r.ResourceId ?? "", cls, Normalize(r.Label));
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    public static string Normalize(string s) => Spaces().Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>4 base-36 characters from FNV-1a 32 over UTF-16 code units (same as the Kotlin version).</summary>
    public static string Hash(string s)
    {
        uint h = 0x811C9DC5;
        foreach (var c in s)
        {
            h ^= c;
            unchecked { h *= 0x01000193; }
        }
        var n = h % (36u * 36 * 36 * 36);
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var o = new char[4];
        for (var i = 3; i >= 0; i--) { o[i] = digits[(int)(n % 36)]; n /= 36; }
        return new string(o);
    }

    /// <summary>A gone ID: the same element as it is now (same role, class and resource id; closest label and place).</summary>
    public static Element? Refind(Element old, Snapshot now)
    {
        var oldLabel = Normalize(old.Label);
        Element? best = null;
        var bestScore = -1.0;
        foreach (var c in now.Elements)
        {
            if (c.Role != old.Role || c.Raw.ClassName != old.Raw.ClassName || c.Raw.ResourceId != old.Raw.ResourceId) continue;
            var sim = Similarity(oldLabel, Normalize(c.Label));
            var dist = Math.Sqrt(Math.Pow(c.Bounds.Cx - old.Bounds.Cx, 2) + Math.Pow(c.Bounds.Cy - old.Bounds.Cy, 2));
            var near = 1.0 - Math.Min(dist / 1500.0, 1.0);
            var score = sim * 0.7 + near * 0.3;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        var need = string.IsNullOrEmpty(old.Raw.ResourceId) ? 0.6 : 0.45;
        return bestScore >= need ? best : null;
    }

    /// <summary>1 = same, 0 = nothing alike (shared-bigram Dice score).</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length < 2 || b.Length < 2) return 0.0;
        var ga = new Dictionary<string, int>();
        for (var i = 0; i < a.Length - 1; i++) { var k = a.Substring(i, 2); ga[k] = ga.GetValueOrDefault(k) + 1; }
        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var k = b.Substring(i, 2);
            if (ga.TryGetValue(k, out var v) && v > 0) { shared++; ga[k] = v - 1; }
        }
        return 2.0 * shared / (a.Length - 1 + b.Length - 1);
    }
}
