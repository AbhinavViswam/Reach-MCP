namespace Reach.Core;

/*
 * The screen as plain data, the same model as Reach for Android (android/.../screen/Element.kt), so IDs, diffs
 * and verdicts behave the same on both. Contract: spec/CONTRACT.md §2–§4.
 */

public readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Cx => (Left + Right) / 2;
    public int Cy => (Top + Bottom) / 2;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public enum Role { Button, Field, Password, Switch, Checkbox, Radio, Tab, Item, List, Text, Image, Menu, Link }

public static class Roles
{
    public static string Word(this Role r) => r.ToString().ToLowerInvariant();
    public static bool Pressable(this Role r) => r is not (Role.Text or Role.List or Role.Image);
}

/// <summary>One element as read, before it has an ID.</summary>
public sealed record RawElement(
    Role Role,
    string Label,
    string? Value,
    string? ResourceId,
    string ClassName,
    Box Bounds,
    bool? Checked = null,
    bool Selected = false,
    bool Enabled = true,
    bool Focused = false,
    bool Scrollable = false,
    bool? Expanded = null);

public sealed record Element(string Id, RawElement Raw)
{
    public Role Role => Raw.Role;
    public string Label => Raw.Label;
    public string? Value => Raw.Value;
    public Box Bounds => Raw.Bounds;

    /// <summary>Everything that can change without the element becoming a different one.</summary>
    public string State
    {
        get
        {
            var s = new System.Text.StringBuilder();
            if (Raw.Value != null) s.Append('=').Append(Raw.Value);
            if (Raw.Checked is bool c) s.Append(c ? " on" : " off");
            if (Raw.Expanded is bool e) s.Append(e ? " expanded" : " collapsed");
            if (Raw.Selected) s.Append(" selected");
            if (!Raw.Enabled) s.Append(" disabled");
            if (Raw.Focused) s.Append(" focused");
            return s.ToString();
        }
    }
}

/// <summary>A window drawn over the target window (a dialog, a "Save?" prompt).</summary>
public sealed record Popup(string WindowId, string Title, string? App);

/// <summary>One read of one window.</summary>
public sealed record Snapshot(
    string? App,
    string Title,
    string WindowId,
    Box Window,
    string Monitor,
    IReadOnlyList<Element> Elements,
    string? BlockedApp = null,
    int Version = 0,
    IReadOnlyList<Popup>? Popups = null,
    bool Foreground = false,
    bool Ocr = false)
{
    private Dictionary<string, Element>? _byId;
    public IReadOnlyDictionary<string, Element> ById => _byId ??= Elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    public IReadOnlyList<Popup> PopupList => Popups ?? Array.Empty<Popup>();

    public int Signature
    {
        get
        {
            var h = new HashCode();
            h.Add(App);
            h.Add(Title);
            foreach (var e in Elements) { h.Add(e.Id); h.Add(e.State); }
            foreach (var p in PopupList) h.Add(p.WindowId);
            return h.ToHashCode();
        }
    }
}
