using System.Drawing;
using Reach.Mcp;

namespace Reach.Deep;

/// <summary>
/// Asks the user before a risky action (send mail, run a command, delete, close a program…): a small always-on-top
/// box with Allow and Deny. No answer in 30 s means no. The model never sees or changes this.
/// </summary>
internal static class Approve
{
    private const int TimeoutSeconds = 30;

    public static void Require(string what, string detail)
    {
        if (!Ask(what, detail))
            throw new ToolError("denied", $"the user did not allow: {what} (or didn't answer within {TimeoutSeconds} s); don't retry unless they ask");
    }

    public static bool Ask(string what, string detail)
    {
        var allowed = false;
        var t = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var f = new Form
            {
                Text = "Reach — allow this?",
                TopMost = true,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(460, 210),
                ShowInTaskbar = true,
            };
            var title = new Label { Text = $"Allow your agent to {what}?", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = false, Bounds = new Rectangle(16, 12, 428, 44) };
            var body = new TextBox { Text = detail, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Bounds = new Rectangle(16, 60, 428, 90), BorderStyle = BorderStyle.FixedSingle };
            var deny = new Button { Text = "Deny", Bounds = new Rectangle(248, 162, 92, 32), DialogResult = DialogResult.No };
            var allow = new Button { Text = "Allow", Bounds = new Rectangle(352, 162, 92, 32), DialogResult = DialogResult.Yes };
            var left = TimeoutSeconds;
            var count = new Label { Text = $"No answer in {left} s = Deny", AutoSize = true, Location = new Point(16, 170), ForeColor = SystemColors.GrayText };
            f.Controls.AddRange(new Control[] { title, body, deny, allow, count });
            f.AcceptButton = deny; // Enter must never approve by accident.
            f.CancelButton = deny;
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) =>
            {
                left--;
                count.Text = $"No answer in {left} s = Deny";
                if (left <= 0) { f.DialogResult = DialogResult.No; f.Close(); }
            };
            f.Shown += (_, _) => { f.Activate(); deny.Focus(); timer.Start(); };
            allowed = f.ShowDialog() == DialogResult.Yes;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(TimeoutSeconds + 5));
        Log(what, detail, allowed);
        return allowed;
    }

    /// <summary>Every request and its answer, on this PC only (the full audit log comes in P5).</summary>
    private static void Log(string what, string detail, bool allowed)
    {
        try
        {
            Directory.CreateDirectory(Core.Config.Dir);
            File.AppendAllText(Path.Combine(Core.Config.Dir, "approvals.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(allowed ? "ALLOWED" : "denied")} {what} :: {detail.Replace('\n', ' ')}\n");
        }
        catch { }
    }
}
