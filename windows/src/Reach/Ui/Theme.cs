using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Reach.Ui;

/// <summary>
/// Light and dark colours for Reach's windows, following the Windows app mode (Settings › Personalisation › Colours).
/// Controls marked with Tag "card", "sub", "primary" or "danger" get those roles.
/// </summary>
internal static class Theme
{
    public static bool Dark
    {
        get
        {
            var forced = Environment.GetEnvironmentVariable("REACH_THEME");
            if (forced is "dark" or "light") return forced == "dark";
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    public static Color Back => Dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xF3, 0xF3, 0xF3);
    public static Color Card => Dark ? Color.FromArgb(0x2A, 0x2A, 0x2A) : Color.White;
    public static Color Field => Dark ? Color.FromArgb(0x33, 0x33, 0x33) : Color.FromArgb(0xF7, 0xF7, 0xF7);
    public static Color Text => Dark ? Color.FromArgb(0xF1, 0xF1, 0xF1) : Color.FromArgb(0x1B, 0x1B, 0x1B);
    public static Color Sub => Dark ? Color.FromArgb(0xA8, 0xA8, 0xA8) : Color.FromArgb(0x5F, 0x5F, 0x5F);
    public static Color Line => Dark ? Color.FromArgb(0x44, 0x44, 0x44) : Color.FromArgb(0xD6, 0xD6, 0xD6);
    public static Color Accent => Dark ? Color.FromArgb(0x5E, 0xE6, 0xC8) : Color.FromArgb(0x00, 0x6B, 0x58);
    public static Color OnAccent => Dark ? Color.FromArgb(0x00, 0x38, 0x2E) : Color.White;
    public static Color Danger => Dark ? Color.FromArgb(0xF2, 0xB8, 0xB5) : Color.FromArgb(0xB3, 0x26, 0x1E);
    public static Color OnDanger => Dark ? Color.FromArgb(0x60, 0x14, 0x10) : Color.White;

    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Heading = new("Segoe UI Semibold", 12f);
    public static readonly Font Big = new("Segoe UI Semibold", 16f);
    public static readonly Font Mono = new("Cascadia Mono", 9f);

    public static void Apply(Control root)
    {
        if (root is Form f)
        {
            f.Font = Body;
            f.HandleCreated += (_, _) => DarkTitleBar(f.Handle, Dark);
            if (f.IsHandleCreated) DarkTitleBar(f.Handle, Dark);
        }
        Paint(root, Back);
    }

    private static void Paint(Control c, Color parentBack)
    {
        var tag = c.Tag as string;
        switch (c)
        {
            case Form:
                c.BackColor = Back; c.ForeColor = Text; break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = tag is "primary" or "danger" ? 0 : 1;
                b.FlatAppearance.BorderColor = Line;
                b.BackColor = tag == "primary" ? Accent : tag == "danger" ? Danger : Field;
                b.ForeColor = tag == "primary" ? OnAccent : tag == "danger" ? OnDanger : Text;
                b.Cursor = Cursors.Hand;
                break;
            case TextBox t:
                t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Field; cb.ForeColor = Text; break;
            case ListBox lb:
                lb.BackColor = Field; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.FixedSingle; break;
            case ListView lv:
                lv.BackColor = Field; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.FixedSingle; DarkHeaders(lv); break;
            case CheckBox ch:
                ch.FlatStyle = FlatStyle.Flat; ch.ForeColor = Text; ch.BackColor = parentBack;
                ch.FlatAppearance.BorderColor = Sub; ch.FlatAppearance.CheckedBackColor = Accent;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                l.ForeColor = tag == "sub" || l.ForeColor == SystemColors.GrayText ? Sub : tag == "accent" ? Accent : tag == "danger" ? Danger : Text;
                break;
            default:
                c.BackColor = tag == "card" ? Card : parentBack;
                c.ForeColor = Text;
                break;
        }
        foreach (Control k in c.Controls) Paint(k, c.BackColor);
    }

    /// <summary>List headers drawn in the theme's colours (Windows draws them light otherwise).</summary>
    private static void DarkHeaders(ListView lv)
    {
        if (lv.OwnerDraw) return;
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using var back = new SolidBrush(Card);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var line = new Pen(Line);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Bold, Rectangle.Inflate(e.Bounds, -6, 0), Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void DarkTitleBar(IntPtr hwnd, bool dark)
    {
        var v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
    }

    /// <summary>The tray and window icon: a rounded square with an R, grey with two bars while paused.</summary>
    public static Icon MakeIcon(bool paused, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = new RectangleF(1, 1, size - 2, size - 2);
            using var path = Rounded(r, size / 4f);
            using var fill = new SolidBrush(paused ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x00, 0x8C, 0x73));
            g.FillPath(fill, path);
            if (paused)
            {
                var w = size * 0.14f;
                var h = size * 0.5f;
                g.FillRectangle(Brushes.White, size * 0.31f, size * 0.25f, w, h);
                g.FillRectangle(Brushes.White, size * 0.55f, size * 0.25f, w, h);
            }
            else
            {
                using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("R", font, Brushes.White, new RectangleF(0, size * 0.02f, size, size), sf);
            }
        }
        var h2 = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h2).Clone();
        DestroyIcon(h2);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
