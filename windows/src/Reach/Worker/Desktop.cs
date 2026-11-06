using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Reach.Core;
using Reach.Mcp;
using static Reach.Worker.Native;

namespace Reach.Worker;

internal sealed record Win(IntPtr Handle, string Id, string Title, string App, Box Rect, bool Minimized);

/// <summary>Top-level windows: listing, IDs, focus, popups, monitors.</summary>
internal static class Desktop
{
    private static readonly Dictionary<uint, string> ProcNames = new();

    public static string IdOf(IntPtr h) => "w" + Convert.ToString(h.ToInt64(), 16);

    public static IntPtr? Parse(string id)
    {
        if (id.Length < 2 || id[0] != 'w') return null;
        try { return new IntPtr(Convert.ToInt64(id[1..], 16)); } catch { return null; }
    }

    public static string AppOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        lock (ProcNames)
        {
            if (ProcNames.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { n = "unknown"; }
            if (ProcNames.Count > 500) ProcNames.Clear();
            ProcNames[pid] = n;
            return n;
        }
    }

    public static Box Bounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r);
        return new Box(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    public static Win Info(IntPtr h) => new(h, IdOf(h), Text(h), AppOf(h), Bounds(h), IsIconic(h));

    /// <summary>Windows a person would call open windows: visible, titled, not hidden UWP shells or tool palettes.</summary>
    public static List<Win> Windows()
    {
        var list = new List<Win>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Cloaked(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            var w = Info(h);
            if (w.Rect.IsEmpty && !w.Minimized) return true;
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Windows owned by [h] that are showing (a "Save?" prompt, a file dialog): they sit over it.</summary>
    public static List<Win> PopupsOf(IntPtr h)
    {
        var list = new List<Win>();
        EnumWindows((w, _) =>
        {
            if (w == h || !IsWindowVisible(w) || Cloaked(w)) return true;
            var owner = GetWindow(w, GW_OWNER);
            var hops = 0;
            while (owner != IntPtr.Zero && owner != h && hops++ < 5) owner = GetWindow(owner, GW_OWNER);
            if (owner == h && !Bounds(w).IsEmpty) list.Add(Info(w));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>A window id (w…), else words from a title or app name. Front-most match wins.</summary>
    public static Win? Match(string q)
    {
        if (Parse(q) is IntPtr h && IsWindow(h)) return Info(h);
        var n = q.ToLowerInvariant();
        var all = Windows();
        return all.FirstOrDefault(w => w.App == n || w.App == n.Replace(".exe", ""))
               ?? all.FirstOrDefault(w => w.Title.ToLowerInvariant().Contains(n))
               ?? all.FirstOrDefault(w => w.App.Contains(n.Replace(" ", "")));
    }

    /// <summary>Brings [h] to the front. Windows only allows that to the front app's thread, so it borrows it.</summary>
    public static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (GetForegroundWindow() == h) return true;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
        for (var i = 0; i < 10 && GetForegroundWindow() != h; i++) Thread.Sleep(30);
        return GetForegroundWindow() == h;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);

    public static int ScalePercent(IntPtr h)
    {
        var dpi = GetDpiForWindow(h);
        return dpi == 0 ? 100 : (int)Math.Round(dpi * 100.0 / 96);
    }

    public static string MonitorText(IntPtr h)
    {
        var screens = Screen.AllScreens;
        var s = Screen.FromHandle(h);
        var i = Array.IndexOf(screens, s) + 1;
        return $"monitor {i} of {screens.Length} ({ScalePercent(h)}%)";
    }

    public static IEnumerable<string> Monitors()
    {
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var mon = MonitorFromPoint(new POINT { X = b.Left + 1, Y = b.Top + 1 }, 2);
            var scale = GetDpiForMonitor(mon, 0, out var dx, out _) == 0 ? (int)Math.Round(dx * 100.0 / 96) : 100;
            yield return $"monitor {i + 1}: {b.Width}×{b.Height} at {b.Left},{b.Top} · {scale}%{(screens[i].Primary ? " · main" : "")}";
        }
    }
}

/// <summary>
/// Real mouse and keyboard, the last resort. Waits until the user has paused (so Reach never fights them), and
/// puts the cursor back where it was.
/// </summary>
internal static class Input
{
    private static uint _ownTick;

    /// <summary>Waits up to 3 s for 0.8 s without the user's own input.</summary>
    public static void WaitForUserPause()
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            GetLastInputInfo(ref info);
            var ours = _ownTick != 0 && Math.Abs((int)(info.dwTime - _ownTick)) < 150;
            var idle = (uint)Environment.TickCount - info.dwTime;
            if (ours || idle >= 800) return;
            if (sw.ElapsedMilliseconds > 3_000)
                throw new ToolError("user_active", "the user is using the mouse or keyboard right now; try again in a moment");
            Thread.Sleep(100);
        }
    }

    public static uint IdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        GetLastInputInfo(ref info);
        return (uint)Environment.TickCount - info.dwTime;
    }

    private static void Send(params INPUT[] inputs)
    {
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        _ownTick = (uint)Environment.TickCount;
    }

    private static INPUT Mouse(uint flags, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data), dwExtraInfo = ReachMark } },
    };

    private static INPUT Key(ushort vk, bool up, bool unicode = false, char ch = '\0')
    {
        var flags = (up ? KEYEVENTF_KEYUP : 0) | (unicode ? KEYEVENTF_UNICODE : 0) | (!unicode && Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = unicode ? (ushort)0 : vk, wScan = unicode ? ch : (ushort)0, dwFlags = flags, dwExtraInfo = ReachMark } },
        };
    }

    private static readonly HashSet<ushort> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D };

    private static void WithCursor(Action a)
    {
        GetCursorPos(out var saved);
        try { a(); }
        finally
        {
            Thread.Sleep(60);
            SetCursorPos(saved.X, saved.Y);
        }
    }

    public static void Click(int x, int y, bool right = false, int count = 1) => WithCursor(() =>
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        for (var i = 0; i < count; i++)
        {
            Send(Mouse(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN), Mouse(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP));
            if (count > 1) Thread.Sleep(60);
        }
    });

    public static void Hover(int x, int y)
    {
        SetCursorPos(x, y);
        _ownTick = (uint)Environment.TickCount;
    }

    public static void Drag(int x1, int y1, int x2, int y2, int ms) => WithCursor(() =>
    {
        SetCursorPos(x1, y1);
        Thread.Sleep(40);
        Send(Mouse(MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(120);
        var steps = Math.Clamp(ms / 15, 8, 200);
        for (var i = 1; i <= steps; i++)
        {
            SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(ms / steps);
        }
        Thread.Sleep(60);
        Send(Mouse(MOUSEEVENTF_LEFTUP));
    });

    /// <summary>Mouse wheel over a point (Windows scrolls the window under the cursor, even when it isn't in front).</summary>
    public static void Wheel(int x, int y, int notches, bool horizontal) => WithCursor(() =>
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, notches * 120));
    });

    /// <summary>Any text, any language: sent as Unicode characters, not key codes.</summary>
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n') { Press(0x0D); continue; }
            if (ch == '\r') continue;
            if (ch == '\t') { Press(0x09); continue; }
            Send(Key(0, false, true, ch), Key(0, true, true, ch));
        }
    }

    public static void Press(ushort vk) => Send(Key(vk, false), Key(vk, true));

    private static readonly Dictionary<string, ushort> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B, ["tab"] = 0x09, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21,
        ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["space"] = 0x20,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B,
        ["menu"] = 0x5D, ["apps"] = 0x5D, ["printscreen"] = 0x2C, ["capslock"] = 0x14, ["back"] = 0xA6,
        ["volume_up"] = 0xAF, ["volume_down"] = 0xAE, ["mute"] = 0xAD, ["play_pause"] = 0xB3, ["next_track"] = 0xB0, ["prev_track"] = 0xB1,
    };

    /// <summary>"enter", "ctrl+s", "ctrl+shift+t", "alt+f4", "win+d", "a", "f5".</summary>
    public static void Combo(string combo)
    {
        var parts = combo.ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ToolError("bad_args", "key is empty");
        var vks = new List<ushort>();
        foreach (var p in parts)
        {
            if (Names.TryGetValue(p, out var vk)) vks.Add(vk);
            else if (p.Length > 1 && p[0] == 'f' && int.TryParse(p[1..], out var f) && f is >= 1 and <= 24) vks.Add((ushort)(0x70 + f - 1));
            else if (p.Length == 1)
            {
                var scan = VkKeyScan(p[0]);
                if (scan == -1) throw new ToolError("bad_args", $"can't press '{p}' as a key; use act type for text");
                if ((scan & 0x100) != 0 && parts.Length == 1) vks.Add(0x10);
                vks.Add((ushort)(scan & 0xFF));
            }
            else throw new ToolError("bad_args", $"unknown key '{p}'; e.g. enter, escape, tab, f5, ctrl+s, alt+f4, win+d");
        }
        var down = vks.Select(v => Key(v, false));
        var up = Enumerable.Reverse(vks).Select(v => Key(v, true));
        Send(down.Concat(up).ToArray());
    }
}

/// <summary>Screenshots of one window (works when it's behind others) or the whole screen.</summary>
internal static class Capture
{
    public sealed record Shot(string Base64, int Width, int Height, double Scale, int OriginX, int OriginY);

    public sealed record Frame(Bitmap Bitmap, int OriginX, int OriginY) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    /// <summary>The window as drawn, even when other windows cover it (PrintWindow); the screen area as a fallback.</summary>
    public static Frame WindowFrame(IntPtr h)
    {
        var vis = Desktop.Bounds(h);
        GetWindowRect(h, out var full);
        if (vis.IsEmpty) throw new ToolError("not_found", "the window has no size (minimized?); switch to it first");
        using var bmp = new Bitmap(full.Right - full.Left, full.Bottom - full.Top, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            ok = PrintWindow(h, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        var crop = new Rectangle(vis.Left - full.Left, vis.Top - full.Top, vis.Width, vis.Height);
        crop.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (!ok || crop.IsEmpty) return AreaFrame(vis);
        return new Frame(bmp.Clone(crop, bmp.PixelFormat), vis.Left, vis.Top);
    }

    public static Frame ScreenFrame()
    {
        var v = SystemInformation.VirtualScreen;
        return AreaFrame(new Box(v.Left, v.Top, v.Right, v.Bottom));
    }

    /// <summary>What is on screen inside [b] (screen pixels): used for zooming into a region.</summary>
    public static Frame AreaFrame(Box b)
    {
        if (b.IsEmpty) throw new ToolError("bad_args", "region has no size");
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, new System.Drawing.Size(b.Width, b.Height));
        return new Frame(bmp, b.Left, b.Top);
    }

    public static Shot Window(IntPtr h) { using var f = WindowFrame(h); return Encode(f.Bitmap, f.OriginX, f.OriginY); }

    public static Shot Screen() { using var f = ScreenFrame(); return Encode(f.Bitmap, f.OriginX, f.OriginY); }

    /// <summary>JPEG with the long side at most 1280 px, which every MCP client accepts.</summary>
    public static Shot Encode(Bitmap src, int ox, int oy, int maxSide = 1280)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        using var outBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        using var ms = new MemoryStream();
        outBmp.Save(ms, codec, ps);
        return new Shot(Convert.ToBase64String(ms.ToArray()), w, h, scale, ox, oy);
    }
}
