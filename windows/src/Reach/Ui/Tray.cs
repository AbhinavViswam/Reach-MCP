using System.Runtime.InteropServices;
using Reach.Core;

namespace Reach.Ui;

/// <summary>
/// The tray icon: shows that an agent may use this PC, with Pause / Resume (also Ctrl+Alt+Backspace from anywhere),
/// Settings, Activity and Quit. Only one Reach per PC shows it (the first to start); another takes over when that
/// one ends. Pause works for every Reach on the PC, because it is a flag file they all read (Core/Safety.cs).
/// </summary>
internal static class Tray
{
    public const string HotkeyText = "Ctrl+Alt+Backspace";

    public static void Start(string mode, Func<string> status)
    {
        if (!Config.Current.Tray) return;
        var t = new Thread(() => Run(mode, status)) { IsBackground = true, Name = "tray" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static void Run(string mode, Func<string> status)
    {
        var mutex = new Mutex(false, @"Local\ReachTray");
        while (true)
        {
            try { if (mutex.WaitOne(TimeSpan.FromSeconds(5))) break; }
            catch (AbandonedMutexException) { break; } // the last owner ended without letting go: ours now
        }
        try
        {
            Application.EnableVisualStyles();
            Application.Run(new TrayContext(mode, status));
        }
        catch (Exception e) { Console.Error.WriteLine("tray: " + e.Message); }
        finally { try { mutex.ReleaseMutex(); } catch { } }
    }

    /// <summary>Turns the kill switch over and says so, from the tray, the hotkey or the settings window.</summary>
    public static void TogglePause(NotifyIcon? icon = null)
    {
        var on = !Pause.IsOn;
        Pause.Set(on);
        Audit.Note(on ? "paused by the user" : "resumed by the user");
        icon?.ShowBalloonTip(3000, on ? "Reach paused" : "Reach resumed",
            on ? "Your agent can't use this PC until you resume. A running action was stopped." : "Your agent can use this PC again.",
            ToolTipIcon.Info);
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly HotkeyWindow _hotkey;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ToolStripMenuItem _status, _pause;
        private readonly Func<string> _statusText;
        private readonly System.Drawing.Icon _on = Theme.MakeIcon(false, SystemInformation.SmallIconSize.Width * 2);
        private readonly System.Drawing.Icon _off = Theme.MakeIcon(true, SystemInformation.SmallIconSize.Width * 2);

        public TrayContext(string mode, Func<string> statusText)
        {
            _statusText = statusText;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem($"Reach for Windows {Program.Version} · {mode}") { Enabled = false });
            _status = new ToolStripMenuItem("") { Enabled = false };
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());
            _pause = new ToolStripMenuItem("Pause agent access", null, (_, _) => { TogglePause(_icon); Refresh(); }) { ShortcutKeyDisplayString = HotkeyText };
            menu.Items.Add(_pause);
            menu.Items.Add("Settings…", null, (_, _) => SettingsForm.ShowOne());
            menu.Items.Add("Activity…", null, (_, _) => ActivityForm.ShowOne());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit this Reach (the agent loses this PC)", null, (_, _) =>
            {
                Audit.Note("Reach quit from the tray");
                _icon!.Visible = false;
                Environment.Exit(0);
            });
            _icon = new NotifyIcon { Icon = _on, Visible = true, ContextMenuStrip = menu, Text = "Reach" };
            _icon.DoubleClick += (_, _) => SettingsForm.ShowOne();
            _hotkey = new HotkeyWindow(() => { TogglePause(_icon); Refresh(); });
            if (!_hotkey.Registered) _pause.ShortcutKeyDisplayString = "hotkey taken by another app";
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
        }

        private void Refresh()
        {
            var paused = Pause.IsOn;
            _pause.Text = paused ? "Resume agent access" : "Pause agent access";
            _status.Text = paused ? "Paused: every call is refused" : _statusText();
            _icon.Icon = paused ? _off : _on;
            var tip = paused ? "Reach · paused" : "Reach · " + _statusText();
            _icon.Text = tip.Length > 120 ? tip[..120] : tip;
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _hotkey.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>A hidden window that receives the global hotkey.</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_BACK = 0x08;
        private readonly Action _pressed;
        public bool Registered { get; }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotkeyWindow(Action pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams());
            Registered = RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_BACK);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Registered) UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }
    }
}
