using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Reach.Core;
using Reach.Mcp;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace Reach.Worker;

/// <summary>A read of a window, plus the live UI Automation elements behind each ID (to act on them).</summary>
internal sealed record Live(Snapshot Snap, IntPtr Hwnd, Dictionary<string, IUIAutomationElement> Nodes, Dictionary<string, IntPtr> Owner);

/// <summary>
/// Reads windows through UI Automation. One cached request per window gets every property at once (fast), and
/// every UIA call has a time limit (IUIAutomation2 timeouts), so a frozen app returns an error instead of hanging.
/// </summary>
internal static class Uia
{
    public static readonly IUIAutomation A;
    public static int FocusEvents;

    private const int MaxElements = 300;
    private const int MaxDepth = 60;
    private const int MaxLabel = 120;
    private const int ReadBudgetMs = 4_000;

    private static readonly int[] Props =
    {
        UIA_NamePropertyId, UIA_ControlTypePropertyId, UIA_AutomationIdPropertyId, UIA_ClassNamePropertyId,
        UIA_BoundingRectanglePropertyId, UIA_IsEnabledPropertyId, UIA_IsOffscreenPropertyId, UIA_IsPasswordPropertyId,
        UIA_HasKeyboardFocusPropertyId, UIA_FrameworkIdPropertyId, UIA_HelpTextPropertyId, UIA_NativeWindowHandlePropertyId,
        UIA_IsInvokePatternAvailablePropertyId, UIA_IsTogglePatternAvailablePropertyId, UIA_ToggleToggleStatePropertyId,
        UIA_IsValuePatternAvailablePropertyId, UIA_ValueValuePropertyId, UIA_ValueIsReadOnlyPropertyId,
        UIA_IsSelectionItemPatternAvailablePropertyId, UIA_SelectionItemIsSelectedPropertyId,
        UIA_IsScrollPatternAvailablePropertyId, UIA_ScrollVerticallyScrollablePropertyId, UIA_ScrollHorizontallyScrollablePropertyId,
        UIA_IsExpandCollapsePatternAvailablePropertyId, UIA_ExpandCollapseExpandCollapseStatePropertyId,
        UIA_IsRangeValuePatternAvailablePropertyId, UIA_RangeValueValuePropertyId,
    };

    static Uia()
    {
        var a = new CUIAutomation8();
        var a2 = (IUIAutomation2)a;
        a2.ConnectionTimeout = 2_000;
        a2.TransactionTimeout = 3_000;
        // Never let a read move the keyboard focus.
        a2.AutoSetFocus = 0;
        A = a;
    }

    public static void StartEvents()
    {
        try { A.AddFocusChangedEventHandler(null, new FocusCounter()); }
        catch (Exception e) { Console.Error.WriteLine("focus events unavailable: " + e.Message); }
    }

    [ComVisible(true)]
    private sealed class FocusCounter : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => Interlocked.Increment(ref FocusEvents);
    }

    private static IUIAutomationCacheRequest Request()
    {
        var cr = A.CreateCacheRequest();
        foreach (var p in Props) cr.AddProperty(p);
        cr.TreeScope = TreeScope.TreeScope_Subtree;
        cr.TreeFilter = A.ControlViewCondition;
        cr.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        return cr;
    }

    // -------------------------------------------------------------------------------------------- reading

    public static Live Read(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) throw new ToolError("not_found", "the working window is gone; apps list or observe window=…");
        var w = Desktop.Info(hwnd);
        var popups = Desktop.PopupsOf(hwnd);
        var fg = Native.GetForegroundWindow() == hwnd || popups.Any(p => p.Handle == Native.GetForegroundWindow());
        var monitor = Desktop.MonitorText(hwnd);
        var popupList = popups.Select(p => new Popup(p.Id, p.Title, p.App)).ToList();
        if (Privacy.IsBlocked(w.App, w.Title))
            return new Live(new Snapshot(w.App, w.Title, w.Id, w.Rect, monitor, Array.Empty<Element>(), w.App, Popups: popupList, Foreground: fg),
                hwnd, new(), new());

        var raws = new List<RawElement>();
        var nodes = new List<IUIAutomationElement>();
        var owners = new List<IntPtr>();
        var sw = Stopwatch.StartNew();
        var partial = false;
        // Popups first: they're what the user sees on top, and what needs answering.
        foreach (var p in popups.Where(p => !Privacy.IsBlocked(p.App, p.Title)))
            partial |= ReadWindow(p.Handle, raws, nodes, owners, sw);
        partial |= ReadWindow(hwnd, raws, nodes, owners, sw);

        var elements = Ids.Assign(w.App, raws);
        var map = new Dictionary<string, IUIAutomationElement>();
        var own = new Dictionary<string, IntPtr>();
        for (var i = 0; i < elements.Count; i++) { map[elements[i].Id] = nodes[i]; own[elements[i].Id] = owners[i]; }
        var title = partial ? w.Title + " (partial read: too many elements or slow app)" : w.Title;
        return new Live(new Snapshot(w.App, title, w.Id, w.Rect, monitor, elements, Popups: popupList, Foreground: fg), hwnd, map, own);
    }

    /// <returns>true when the read stopped early (too many elements, or out of time).</returns>
    private static bool ReadWindow(IntPtr h, List<RawElement> raws, List<IUIAutomationElement> nodes, List<IntPtr> owners, Stopwatch sw)
    {
        IUIAutomationElement root;
        try { root = A.ElementFromHandleBuildCache(h, Request()); }
        catch (COMException e) when ((uint)e.HResult == 0x80070005)
        {
            throw new ToolError("no_access", "this window belongs to an app running as administrator; Reach runs without admin rights and can't read it");
        }
        catch (COMException e)
        {
            throw new ToolError("timeout", $"the window didn't answer UI Automation (0x{e.HResult:x8}); it may be busy or frozen");
        }
        var before = raws.Count;
        var stopped = Walk(root, raws, nodes, 0, false, sw);
        for (var i = before; i < raws.Count; i++) owners.Add(h);
        return stopped;
    }

    private static bool Walk(IUIAutomationElement n, List<RawElement> raws, List<IUIAutomationElement> nodes, int depth, bool pressableOnly, Stopwatch sw)
    {
        if (raws.Count >= MaxElements || sw.ElapsedMilliseconds > ReadBudgetMs) return true;
        if (depth > MaxDepth) return false;
        var type = n.CachedControlType;
        if (type == UIA_ScrollBarControlTypeId || type == UIA_ThumbControlTypeId) return false;
        var offscreen = n.CachedIsOffscreen != 0;
        var rect = n.CachedBoundingRectangle;
        var box = new Box(rect.left, rect.top, rect.right, rect.bottom);

        if (!offscreen && !box.IsEmpty && depth > 0)
        {
            var role = RoleOf(n, type);
            if (role != null && !(pressableOnly && !role.Value.Pressable() && role != Role.Field))
            {
                raws.Add(Raw(n, role.Value, box));
                nodes.Add(n);
                if (role is not (Role.Text or Role.List))
                {
                    // A button's own words are its label: inside it, only pressable things get their own line.
                    if (Children(n, raws, nodes, depth, true, sw)) return true;
                    return false;
                }
            }
        }
        return Children(n, raws, nodes, depth, pressableOnly, sw);
    }

    private static bool Children(IUIAutomationElement n, List<RawElement> raws, List<IUIAutomationElement> nodes, int depth, bool pressableOnly, Stopwatch sw)
    {
        var kids = n.GetCachedChildren();
        if (kids == null) return false;
        for (var i = 0; i < kids.Length; i++)
            if (Walk(kids.GetElement(i), raws, nodes, depth + 1, pressableOnly, sw)) return true;
        return false;
    }

    private static object P(IUIAutomationElement n, int id)
    {
        try { return n.GetCachedPropertyValue(id); } catch { return null!; }
    }

    private static bool B(IUIAutomationElement n, int id) => P(n, id) is bool b && b;

    private static Role? RoleOf(IUIAutomationElement n, int type)
    {
        if (n.CachedIsPassword != 0) return Role.Password;
        var name = n.CachedName ?? "";
        var named = !string.IsNullOrWhiteSpace(name);
        var editable = B(n, UIA_IsValuePatternAvailablePropertyId) && !B(n, UIA_ValueIsReadOnlyPropertyId);
        var scrollable = B(n, UIA_ScrollVerticallyScrollablePropertyId) || B(n, UIA_ScrollHorizontallyScrollablePropertyId);
        return type switch
        {
            UIA_EditControlTypeId => Role.Field,
            UIA_DocumentControlTypeId => editable ? Role.Field : (scrollable ? Role.List : named ? Role.Text : null),
            UIA_ComboBoxControlTypeId => editable ? Role.Field : Role.Button,
            UIA_ButtonControlTypeId or UIA_SplitButtonControlTypeId =>
                B(n, UIA_IsTogglePatternAvailablePropertyId) && !B(n, UIA_IsInvokePatternAvailablePropertyId) ? Role.Switch : Role.Button,
            UIA_CheckBoxControlTypeId => Role.Checkbox,
            UIA_RadioButtonControlTypeId => Role.Radio,
            UIA_TabItemControlTypeId => Role.Tab,
            UIA_HyperlinkControlTypeId => Role.Link,
            UIA_MenuItemControlTypeId => Role.Menu,
            UIA_ListItemControlTypeId or UIA_TreeItemControlTypeId or UIA_DataItemControlTypeId => Role.Item,
            UIA_HeaderItemControlTypeId => Role.Button,
            UIA_SliderControlTypeId or UIA_SpinnerControlTypeId => Role.Item,
            UIA_ListControlTypeId or UIA_TreeControlTypeId or UIA_DataGridControlTypeId or UIA_TableControlTypeId => scrollable ? Role.List : null,
            UIA_TextControlTypeId => named ? Role.Text : null,
            UIA_ImageControlTypeId => named ? Role.Image : null,
            _ when B(n, UIA_IsInvokePatternAvailablePropertyId) && named => Role.Item,
            _ when B(n, UIA_IsTogglePatternAvailablePropertyId) && named => Role.Switch,
            _ when scrollable && B(n, UIA_IsScrollPatternAvailablePropertyId) => Role.List,
            _ => null,
        };
    }

    private static RawElement Raw(IUIAutomationElement n, Role role, Box box)
    {
        var name = Clean(n.CachedName ?? "");
        var label = role switch
        {
            Role.Field or Role.Password => name.Length > 0 ? name : Clean(n.CachedHelpText ?? ""),
            _ when name.Length > 0 => name,
            _ => InnerWords(n),
        };
        string? value = null;
        if (role == Role.Field || (role == Role.Button && n.CachedControlType == UIA_ComboBoxControlTypeId))
        {
            var v = P(n, UIA_ValueValuePropertyId) as string ?? "";
            value = v.Length > 500 ? v[..500] + "…" : v;
            if (role == Role.Button && value.Length == 0) value = null;
        }
        else if (B(n, UIA_IsRangeValuePatternAvailablePropertyId) && P(n, UIA_RangeValueValuePropertyId) is double d)
        {
            value = d == Math.Floor(d) ? ((long)d).ToString() : d.ToString("0.##");
        }

        bool? isChecked = null;
        if (B(n, UIA_IsTogglePatternAvailablePropertyId) && P(n, UIA_ToggleToggleStatePropertyId) is int t && t != 2) isChecked = t == 1;
        else if (role == Role.Radio) isChecked = B(n, UIA_SelectionItemIsSelectedPropertyId);

        bool? expanded = null;
        if (B(n, UIA_IsExpandCollapsePatternAvailablePropertyId) && P(n, UIA_ExpandCollapseExpandCollapseStatePropertyId) is int e && e is 0 or 1)
            expanded = e == 1;

        var framework = n.CachedFrameworkId ?? "";
        var aid = n.CachedAutomationId;
        // Chromium/Electron AutomationIds are made fresh on each render: useless (and harmful) for stable IDs.
        if (framework == "Chrome" || string.IsNullOrWhiteSpace(aid)) aid = null;

        return new RawElement(
            Role: role,
            Label: label,
            Value: role == Role.Password ? null : value,
            ResourceId: aid,
            ClassName: n.CachedClassName ?? "",
            Bounds: box,
            Checked: role == Role.Radio ? isChecked : isChecked,
            Selected: role != Role.Radio && B(n, UIA_SelectionItemIsSelectedPropertyId),
            Enabled: n.CachedIsEnabled != 0,
            Focused: n.CachedHasKeyboardFocus != 0 && role is Role.Field or Role.Password,
            Scrollable: B(n, UIA_ScrollVerticallyScrollablePropertyId) || B(n, UIA_ScrollHorizontallyScrollablePropertyId),
            Expanded: expanded);
    }

    /// <summary>Words inside a pressable thing with none of its own (a list row: name · date · size).</summary>
    private static string InnerWords(IUIAutomationElement n)
    {
        var o = new System.Text.StringBuilder();
        void Go(IUIAutomationElement e, int depth)
        {
            if (depth > 5 || o.Length > MaxLabel) return;
            var kids = e.GetCachedChildren();
            if (kids == null) return;
            for (var i = 0; i < kids.Length; i++)
            {
                var c = kids.GetElement(i);
                if (c.CachedIsPassword != 0) continue;
                var w = Clean(c.CachedName ?? "");
                if (w.Length > 0)
                {
                    if (o.Length > 0) o.Append(" · ");
                    o.Append(w);
                }
                Go(c, depth + 1);
            }
        }
        Go(n, 0);
        return Clean(o.ToString());
    }

    private static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > MaxLabel ? s[..MaxLabel] : s;
    }
}
