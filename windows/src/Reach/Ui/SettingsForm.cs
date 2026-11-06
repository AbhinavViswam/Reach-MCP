using System.Diagnostics;
using System.Drawing;
using Reach.Core;

namespace Reach.Ui;

/// <summary>
/// Reach settings (reach.exe --settings, the tray icon, or double-clicking reach.exe): the kill switch, one switch
/// per ability, the folders, app rules, where approvals are asked, the activity log and how to connect an agent.
/// Every change is saved at once and counts from the agent's next call.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static SettingsForm? _open;

    public static void ShowOne()
    {
        if (_open is { IsDisposed: false }) { _open.WindowState = FormWindowState.Normal; _open.Activate(); return; }
        _open = new SettingsForm();
        _open.Show();
    }

    private readonly FlowLayoutPanel _flow;
    private readonly Label _state;
    private readonly Button _pauseButton;
    private readonly System.Windows.Forms.Timer _timer;

    public SettingsForm()
    {
        Text = "Reach settings";
        Icon = Theme.MakeIcon(false);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 760);
        MinimumSize = new Size(480, 400);

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 12, 16, 16),
        };
        Controls.Add(_flow);

        _flow.Controls.Add(new Label { Text = "Reach for Windows", Font = Theme.Big, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        _flow.Controls.Add(Sub($"Version {Program.Version}. Changes are saved at once and count from your agent's next call. " +
                               "Reconnect your agent to see tools you turned on."));

        // ------------------------------------------------------------------ kill switch
        var (status, sb) = Card("Agent access");
        _state = new Label { Font = Theme.Heading, AutoSize = true };
        _pauseButton = new Button { AutoSize = true, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 6, 0, 4) };
        _pauseButton.Click += (_, _) => { Tray.TogglePause(); RefreshState(); };
        sb.Controls.Add(_state);
        sb.Controls.Add(_pauseButton);
        sb.Controls.Add(Sub($"Pausing stops a running action at once and refuses every call until you resume. " +
                            $"Also from the tray icon, or {Tray.HotkeyText} anywhere."));
        _flow.Controls.Add(status);

        // ------------------------------------------------------------------ abilities
        var cfg = Config.Load();
        var (ab, abb) = Card("Abilities");
        abb.Controls.Add(Sub("Seeing and using windows (observe, act, find, apps) is always on while Reach runs. These are extra:"));
        foreach (var (key, label, about) in Config.Abilities)
            abb.Controls.Add(Switch(label, about, cfg.Allows(key), on => Change(c => c.Set(key, on))));
        abb.Controls.Add(Switch("Real mouse and keyboard",
            "When a background click doesn't work, Reach waits until you stop using the mouse, then uses it. Off: background only.",
            cfg.RealInput, on => Change(c => c.RealInput = on)));
        abb.Controls.Add(Switch("Tray icon", "Pause and settings from the taskbar. Counts from the next start.",
            cfg.Tray, on => Change(c => c.Tray = on)));
        _flow.Controls.Add(ab);

        // ------------------------------------------------------------------ folders
        var (fo, fob) = Card("Folders the Files ability may use");
        var folders = new ListBox { Height = 110, Width = 520, IntegralHeight = false };
        void LoadFolders() { folders.Items.Clear(); foreach (var f in Config.Load().FileFolders) folders.Items.Add(f); }
        LoadFolders();
        fob.Controls.Add(folders);
        var frow = Row();
        frow.Controls.Add(Btn("Add folder…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "A folder your agent may read and change files in" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Change(c => { if (!c.FileFolders.Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) c.FileFolders.Add(d.SelectedPath); });
            LoadFolders();
        }));
        frow.Controls.Add(Btn("Remove", () =>
        {
            if (folders.SelectedItem is not string s) return;
            Change(c => c.FileFolders.RemoveAll(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)));
            LoadFolders();
        }));
        fob.Controls.Add(frow);
        fob.Controls.Add(Sub("Names like Documents mean your own folder. Sub-folders count too; nothing outside these."));
        _flow.Controls.Add(fo);

        // ------------------------------------------------------------------ app rules
        var (ap, apb) = Card("App rules");
        apb.Controls.Add(Sub("Allow: read and use. Read only: can be read (and scrolled), never clicked or typed into. " +
                             "Block: never read or touched. A rule is a program name (excel) or words in a window title (hdfc bank)."));
        var rules = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 130, Width = 520 };
        rules.Columns.Add("Program or title words", 360);
        rules.Columns.Add("Rule", 140);
        void LoadRules()
        {
            rules.Items.Clear();
            foreach (var (k, v) in Config.Load().Apps.OrderBy(kv => kv.Key))
                rules.Items.Add(new ListViewItem(new[] { k, Pretty(Privacy.Parse(v)) }) { Tag = k });
        }
        LoadRules();
        apb.Controls.Add(rules);
        var arow = Row();
        var name = new ComboBox { Width = 190, DropDownStyle = ComboBoxStyle.DropDown };
        foreach (var p in RunningPrograms()) name.Items.Add(p);
        var rule = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
        rule.Items.AddRange(new object[] { "Allow", "Read only", "Block" });
        rule.SelectedIndex = 1;
        arow.Controls.Add(name);
        arow.Controls.Add(rule);
        arow.Controls.Add(Btn("Add", () =>
        {
            var k = name.Text.Trim();
            if (k.Length < 2) return;
            var r = rule.SelectedIndex switch { 0 => AppRule.Allow, 2 => AppRule.Block, _ => AppRule.ReadOnly };
            Change(c => c.Apps[k] = Privacy.Word(r));
            name.Text = "";
            LoadRules();
        }));
        arow.Controls.Add(Btn("Remove", () =>
        {
            foreach (ListViewItem it in rules.SelectedItems) Change(c => c.Apps.Remove((string)it.Tag!));
            LoadRules();
        }));
        apb.Controls.Add(arow);
        apb.Controls.Add(Sub("Always blocked unless you add them here as Allow: " + Privacy.Builtin + "."));
        _flow.Controls.Add(ap);

        // ------------------------------------------------------------------ phones
        var (ph, phb) = Card("Phones");
        phb.Controls.Add(Sub("Phones running Reach for Android. Your agent here can use them (the phone tool), and they can " +
                             "answer approvals. Copy each phone's token from its Reach app."));
        var phoneList = new ListBox { Height = 70, Width = 520, IntegralHeight = false };
        var approver = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        void LoadPhones()
        {
            var c = Config.Load();
            phoneList.Items.Clear();
            approver.Items.Clear();
            foreach (var p in c.Phones) { phoneList.Items.Add($"{p.Name} · {p.Url}"); approver.Items.Add(p.Name); }
            if (c.Phones.Count == 0) phoneList.Items.Add("(no phone linked yet)");
            var pick = c.ApprovalLink()?.Name;
            if (pick != null) approver.SelectedItem = pick;
        }
        phb.Controls.Add(phoneList);
        var pname = new TextBox { Width = 300, PlaceholderText = "a short name, e.g. pixel" };
        var purl = new TextBox { Width = 300, Text = "http://127.0.0.1:8765" };
        var ptok = new TextBox { Width = 300, UseSystemPasswordChar = true, PlaceholderText = "token from the phone's Reach app" };
        phb.Controls.Add(Labelled("Name", pname));
        phb.Controls.Add(Labelled("Address", purl));
        phb.Controls.Add(Labelled("Token", ptok));
        var phoneResult = Sub("");
        var prow = Row();
        prow.Controls.Add(Btn("Add phone", () =>
        {
            var n = pname.Text.Trim();
            if (n.Length == 0 || ptok.Text.Trim().Length == 0) { phoneResult.Text = "Give the phone a name and its token."; return; }
            Change(c =>
            {
                c.Phones.RemoveAll(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                c.Phones.Add(new Config.PhoneLink { Name = n, Url = purl.Text.Trim(), Token = ptok.Text.Trim() });
            });
            pname.Text = ""; ptok.Text = "";
            phoneResult.Text = $"Added {n}. Reconnect your agent to see the phone tool.";
            LoadPhones();
        }, "primary"));
        prow.Controls.Add(Btn("Remove", () =>
        {
            var i = phoneList.SelectedIndex;
            var c0 = Config.Load();
            if (i < 0 || i >= c0.Phones.Count) return;
            var n = c0.Phones[i].Name;
            Change(c => c.Phones.RemoveAll(x => x.Name == n));
            LoadPhones();
        }));
        prow.Controls.Add(Btn("Test", () =>
        {
            var i = phoneList.SelectedIndex;
            var c0 = Config.Load();
            if (i < 0 || i >= c0.Phones.Count) { phoneResult.Text = "Pick a phone in the list first."; return; }
            var p = c0.Phones[i];
            phoneResult.Text = $"Asking {p.Name}…";
            Task.Run(async () =>
            {
                string msg;
                try
                {
                    var tools = await new McpClient(p.Url, p.Token).ToolsAsync(CancellationToken.None);
                    msg = $"{p.Name} works: {tools.Count} tools ({string.Join(", ", tools.Take(8).Select(t => t.Name))}…).";
                }
                catch (Mcp.ToolError e) { msg = $"{p.Name}: {e.Hint}"; }
                BeginInvoke(() => phoneResult.Text = msg);
            });
        }));
        phb.Controls.Add(prow);
        phb.Controls.Add(phoneResult);
        phb.Controls.Add(Sub("USB: run  adb forward tcp:8765 tcp:8765  and keep http://127.0.0.1:8765. Wi-Fi: turn on Wi-Fi access " +
                             "in the Reach app and use the phone's address (http://192.168.1.20:8765). Tailscale: the phone's " +
                             "100.x address, with Tailscale access on in the Reach app."));
        LoadPhones();
        _flow.Controls.Add(ph);

        // ------------------------------------------------------------------ approvals
        var (aq, aqb) = Card("Approvals");
        aqb.Controls.Add(Sub("Sending mail, PowerShell, closing programs, replacing or deleting files and showing hidden codes " +
                             "always ask first. No answer in 30 s means no."));
        aqb.Controls.Add(Switch("Ask on my phone", "A linked phone shows Allow / Deny. If it can't be reached, this PC asks.",
            cfg.PhoneApprovals, on => Change(c => c.PhoneApprovals = on)));
        approver.SelectedIndexChanged += (_, _) => { if (approver.SelectedItem is string n) Change(c => c.ApprovalPhone = n); };
        aqb.Controls.Add(Labelled("Which phone", approver));
        var testResult = Sub("");
        aqb.Controls.Add(Btn("Send a test to the phone", () =>
        {
            var link = Config.Load().ApprovalLink();
            if (link == null) { testResult.Text = "Link a phone above first."; return; }
            testResult.Text = $"Sent to {link.Name}. Tap Allow or Deny on the phone…";
            Task.Run(() =>
            {
                var (ok, problem) = Deep.Approve.AskPhone(link.Url, link.Token, "check the connection from this PC (a test)", "Nothing happens either way.");
                var msg = ok switch { true => "Works: you tapped Allow.", false => "Works: the phone answered Deny (or no answer in 30 s).", null => "Couldn't reach the phone: " + problem };
                BeginInvoke(() => testResult.Text = msg);
            });
        }));
        aqb.Controls.Add(testResult);
        _flow.Controls.Add(aq);

        // ------------------------------------------------------------------ activity
        var (ac, acb) = Card("Activity");
        acb.Controls.Add(Sub("Every call your agent made, its result, and every approval. Kept on this PC only."));
        var crow = Row();
        crow.Controls.Add(Btn("Open activity", ActivityForm.ShowOne, "primary"));
        crow.Controls.Add(Btn("Open the Reach folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Config.Dir}\"") { UseShellExecute = true })));
        acb.Controls.Add(crow);
        _flow.Controls.Add(ac);

        // ------------------------------------------------------------------ flows and undo
        var (fl, flb) = Card("Flows and undo");
        flb.Controls.Add(Sub("Flows are tasks your agent saved to replay without the model. Undo puts back what Reach changed " +
                             "(files, window places, the clipboard, Excel cells)."));
        var flowList = new ListBox { Height = 90, Width = 520, IntegralHeight = false };
        void LoadFlows()
        {
            flowList.Items.Clear();
            if (Directory.Exists(Server.FlowRunner.Dir))
                foreach (var f in Directory.GetFiles(Server.FlowRunner.Dir, "*.json").OrderBy(x => x)) flowList.Items.Add(Path.GetFileNameWithoutExtension(f));
            if (flowList.Items.Count == 0) flowList.Items.Add("(no flows saved yet)");
        }
        LoadFlows();
        flb.Controls.Add(flowList);
        var undoInfo = Sub("");
        void LoadUndo()
        {
            var e = Journal.All().Take(4).ToList();
            undoInfo.Text = e.Count == 0 ? "Nothing to undo yet." : "Latest changes: " + string.Join(" · ", e.Select(x => x.What + (x.Undone ? " (undone)" : x.Undoable ? "" : " (can't undo)")));
        }
        LoadUndo();
        var flrow = Row();
        flrow.Controls.Add(Btn("Delete flow", () =>
        {
            if (flowList.SelectedItem is not string n || n.StartsWith('(')) return;
            if (MessageBox.Show(this, $"Delete the flow \"{n}\"?", "Reach", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { File.Delete(Path.Combine(Server.FlowRunner.Dir, n + ".json")); } catch { }
            LoadFlows();
        }));
        flrow.Controls.Add(Btn("Undo Reach's last change", () =>
        {
            var e = Journal.All().FirstOrDefault(x => x.Undoable && !x.Undone && x.Kind != "checkpoint");
            if (e == null) { MessageBox.Show(this, "Nothing left to undo.", "Reach"); return; }
            if (MessageBox.Show(this, $"Undo: {e.What}?", "Reach", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { MessageBox.Show(this, Deep.Undo.Run(new System.Text.Json.Nodes.JsonObject { ["action"] = "last" }).Content[0].Text.Replace("verdict: done · ", ""), "Reach"); }
            catch (Mcp.ToolError err) { MessageBox.Show(this, err.Hint, "Reach"); }
            LoadUndo();
        }));
        flb.Controls.Add(flrow);
        flb.Controls.Add(undoInfo);
        _flow.Controls.Add(fl);

        // ------------------------------------------------------------------ connect
        var (co, cob) = Card("Connect an agent");
        var exe = Environment.ProcessPath ?? "reach.exe";
        var cmd = $"claude mcp add reach-windows -- \"{exe}\"";
        cob.Controls.Add(Sub("Claude Code (other agents: run this exe as a stdio MCP server):"));
        cob.Controls.Add(new TextBox { Text = cmd, ReadOnly = true, Width = 520, Font = Theme.Mono });
        var corow = Row();
        corow.Controls.Add(Btn("Copy command", () => Clipboard.SetText(cmd)));
        corow.Controls.Add(Btn("Copy HTTP token", () => Clipboard.SetText(Token.Get())));
        cob.Controls.Add(corow);
        cob.Controls.Add(Sub("HTTP instead (also how a phone uses this PC): reach.exe --http, port 8766, the token as a Bearer header. " +
                             "Who may connect over HTTP:"));
        var net = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
        net.Items.AddRange(new object[] { "This PC only (USB phones: adb reverse tcp:8766 tcp:8766)", "This PC and my Tailscale devices", "Every network (only on networks you trust)" });
        net.SelectedIndex = cfg.Network switch { "tailscale" => 1, "lan" => 2, _ => 0 };
        net.SelectedIndexChanged += (_, _) => Change(c => c.Network = net.SelectedIndex switch { 1 => "tailscale", 2 => "lan", _ => "local" });
        cob.Controls.Add(net);
        cob.Controls.Add(Sub("Counts the next time reach.exe --http starts." + (Net.TailscaleAddress() is string ts ? $" This PC's Tailscale address: {ts}" : "")));
        _flow.Controls.Add(co);

        _flow.Resize += (_, _) => FitWidths();
        Theme.Apply(this);
        FitWidths();
        RefreshState();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
    }

    private void RefreshState()
    {
        var paused = Pause.IsOn;
        _state.Text = paused ? "Paused: your agent can't use this PC" : "On: your agent may use this PC";
        _state.Tag = paused ? "danger" : "accent";
        _state.ForeColor = paused ? Theme.Danger : Theme.Accent;
        _pauseButton.Text = paused ? "Resume" : "Pause now";
        var tag = paused ? "primary" : "danger";
        if ((string?)_pauseButton.Tag != tag)
        {
            _pauseButton.Tag = tag;
            _pauseButton.BackColor = paused ? Theme.Accent : Theme.Danger;
            _pauseButton.ForeColor = paused ? Theme.OnAccent : Theme.OnDanger;
            _pauseButton.FlatStyle = FlatStyle.Flat;
            _pauseButton.FlatAppearance.BorderSize = 0;
        }
    }

    /// <summary>Loads the file, changes it and saves it, so edits made elsewhere (by hand, another window) are kept.</summary>
    private static void Change(Action<Config> edit)
    {
        try
        {
            var c = Config.Load();
            edit(c);
            c.Save();
        }
        catch (Exception e) { MessageBox.Show("Couldn't save settings: " + e.Message, "Reach"); }
    }

    private static string Pretty(AppRule r) => r switch { AppRule.Block => "Block", AppRule.ReadOnly => "Read only", _ => "Allow" };

    private static IEnumerable<string> RunningPrograms()
    {
        try
        {
            return Process.GetProcesses().Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
                .Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------------------ building blocks

    private void FitWidths()
    {
        // ClientSize already leaves out a visible scrollbar; keep room for one that may still appear.
        var w = _flow.ClientSize.Width - _flow.Padding.Horizontal - (_flow.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        foreach (Control c in _flow.Controls)
        {
            if (c is Label l) { l.MaximumSize = new Size(w, 0); continue; }
            // Cards size themselves to their content; pin them to the page width instead.
            c.MinimumSize = new Size(w, 0);
            c.MaximumSize = new Size(w, 0);
            foreach (Control k in c.Controls) Fit(k, w - c.Padding.Horizontal);
        }
    }

    private static void Fit(Control c, int w)
    {
        switch (c)
        {
            case Label l: l.MaximumSize = new Size(w, 0); break;
            case ListBox or ListView or TextBox when c.Width > 400 || c.Tag as string == "wide": c.Width = w; c.Tag ??= "wide"; break;
            case FlowLayoutPanel f when f.FlowDirection == FlowDirection.TopDown:
                f.Width = w;
                foreach (Control k in f.Controls) Fit(k, w - 24);
                break;
        }
    }

    private static (FlowLayoutPanel Card, FlowLayoutPanel Body) Card(string title)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 12, 0, 0),
            Tag = "card",
        };
        card.Controls.Add(new Label { Text = title, Font = Theme.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        return (card, card);
    }

    private static Label Sub(string text) => new() { Text = text, AutoSize = true, Tag = "sub", Margin = new Padding(0, 2, 0, 4) };

    private static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true, Margin = new Padding(0, 4, 0, 4),
    };

    private static Button Btn(string text, Action click, string? role = null)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 0, 8, 0), Tag = role };
        b.Click += (_, _) => click();
        return b;
    }

    private static Control Labelled(string label, Control field)
    {
        var r = Row();
        r.Controls.Add(new Label { Text = label, Width = 120, Margin = new Padding(0, 6, 8, 0) });
        r.Controls.Add(field);
        return r;
    }

    /// <summary>A switch: the name in bold with what it does under it.</summary>
    private static Control Switch(string label, string about, bool on, Action<bool> changed)
    {
        var box = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 2),
        };
        var ch = new CheckBox { Text = label, Checked = on, AutoSize = true, Font = Theme.Bold, Margin = new Padding(0) };
        ch.CheckedChanged += (_, _) => changed(ch.Checked);
        box.Controls.Add(ch);
        box.Controls.Add(new Label { Text = about, AutoSize = true, Tag = "sub", Margin = new Padding(20, 0, 0, 0) });
        return box;
    }
}

/// <summary>The activity log (Core/Safety.cs Audit), newest first.</summary>
internal sealed class ActivityForm : Form
{
    private static ActivityForm? _open;

    public static void ShowOne()
    {
        if (_open is { IsDisposed: false }) { _open.WindowState = FormWindowState.Normal; _open.Activate(); _open.Reload(); return; }
        _open = new ActivityForm();
        _open.Show();
    }

    private readonly ListView _list;
    private readonly Label _count;

    public ActivityForm()
    {
        Text = "Reach activity";
        Icon = Theme.MakeIcon(false);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 560);

        _list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true };
        _list.Columns.Add("Time", 140);
        _list.Columns.Add("What", 130);
        _list.Columns.Add("Details", 330);
        _list.Columns.Add("Result", 230);
        _list.Columns.Add("ms", 60, HorizontalAlignment.Right);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 8) };
        var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        refresh.Click += (_, _) => Reload();
        var clear = new Button { Text = "Clear log", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete the whole activity log?", "Reach", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Audit.Clear();
            Reload();
        };
        var live = new CheckBox { Text = "Keep updating", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 8, 0) };
        _count = new Label { AutoSize = true, Tag = "sub", Margin = new Padding(8, 8, 0, 0) };
        bar.Controls.AddRange(new Control[] { refresh, clear, live, _count });
        Controls.Add(_list);
        Controls.Add(bar);
        Theme.Apply(this);

        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => { if (live.Checked) Reload(); };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
        Reload();
    }

    private string _shown = "";

    private void Reload()
    {
        var rows = Audit.Read(500);
        var key = rows.Count + "|" + (rows.FirstOrDefault()?.Time ?? "") + (rows.FirstOrDefault()?.Tool ?? "");
        if (key == _shown) return;
        _shown = key;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows)
        {
            var what = r.Kind switch { "approval" => "asked: " + r.Tool, "note" => r.Tool, _ => r.Tool };
            var it = new ListViewItem(new[] { r.Time, what, r.Args, r.Result, r.Kind == "call" ? r.Ms.ToString() : "" })
            {
                ToolTipText = $"{r.Args}\n{r.Result}",
                ForeColor = !r.Ok ? Theme.Danger : r.Kind != "call" ? Theme.Accent : Theme.Text,
            };
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _count.Text = rows.Count == 0 ? "Nothing yet." : $"{rows.Count} newest entries · {Audit.PathOf}";
    }
}

/// <summary>reach.exe --ui-snapshot DIR: renders the windows off-screen into PNGs, for docs and layout checks.</summary>
internal sealed class Snapshot : Form
{
    public Snapshot(string dir)
    {
        ShowInTaskbar = false;
        Opacity = 0;
        Load += (_, _) =>
        {
            Directory.CreateDirectory(dir);
            Save(new SettingsForm(), Path.Combine(dir, "settings.png"), 2300);
            Save(new ActivityForm(), Path.Combine(dir, "activity.png"), 0);
            Close();
        };
    }

    private static void Save(Form f, string path, int tall)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-20000, -20000);
        f.ShowInTaskbar = false;
        f.Show();
        Application.DoEvents();
        Control what = f;
        if (tall > 0 && f.Controls[0] is FlowLayoutPanel flow)
        {
            // The whole scrolling page, not just what fits on screen.
            var w = flow.Width;
            flow.AutoScroll = false;
            flow.Dock = DockStyle.None;
            flow.Width = w;
            flow.Height = flow.GetPreferredSize(new Size(w, 0)).Height;
            what = flow;
        }
        using var bmp = new Bitmap(what.Width, what.Height);
        what.DrawToBitmap(bmp, new Rectangle(0, 0, what.Width, what.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        f.Close();
    }
}
