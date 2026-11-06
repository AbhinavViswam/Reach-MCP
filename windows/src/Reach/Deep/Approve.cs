using System.Drawing;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Reach.Core;
using Reach.Mcp;

namespace Reach.Deep;

/// <summary>
/// Asks the user before a risky action (send mail, run a command, delete, close a program…). On the phone (Reach for
/// Android, when turned on in settings) or in a small always-on-top box here, with Allow and Deny. No answer in 30 s
/// means no. If the phone can't be reached the box here asks instead. Every answer goes to the activity log.
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
        Pause.ThrowIfOn();
        var c = Config.Current;
        var note = "";
        if (c.PhoneApprovals && c.ApprovalLink() is { } link)
        {
            var (answer, problem) = AskPhone(link.Url, link.Token, what, detail);
            if (answer is bool a)
            {
                Audit.Approval(what, detail, a, $"the phone ({link.Name})");
                return a;
            }
            note = $"(Your phone couldn't be asked: {problem}.)\n";
        }
        var ok = AskHere(what, note + detail);
        Audit.Approval(what, detail, ok, "this PC");
        return ok;
    }

    /// <summary>
    /// POST {phone}/approve: the phone shows Allow / Deny and answers when the user taps one (or says no after 30 s).
    /// Returns null with the reason when the phone could not be asked at all.
    /// </summary>
    public static (bool? Allowed, string Problem) AskPhone(string url, string token, string what, string detail)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds + 10) };
            var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/approve")
            {
                Content = new StringContent(new JsonObject { ["what"] = what, ["detail"] = detail, ["from"] = Environment.MachineName }.ToJsonString(),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = http.Send(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, "the phone token is wrong");
            if (!resp.IsSuccessStatusCode) return (null, $"the phone said {(int)resp.StatusCode}");
            var body = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            return (JsonNode.Parse(body)?["allowed"]?.GetValue<bool>() == true, "");
        }
        catch (TaskCanceledException)
        {
            // Asked, but no answer came back in time: that is a no, not a reason to ask again here.
            return (false, "");
        }
        catch (Exception e)
        {
            return (null, e is HttpRequestException h ? (h.InnerException?.Message ?? h.Message) : e.Message);
        }
    }

    private static bool AskHere(string what, string detail)
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
            var body = new TextBox { Text = detail.Replace("\n", "\r\n"), ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Bounds = new Rectangle(16, 60, 428, 90), BorderStyle = BorderStyle.FixedSingle };
            var deny = new Button { Text = "Deny", Bounds = new Rectangle(248, 162, 92, 32), DialogResult = DialogResult.No };
            var allow = new Button { Text = "Allow", Bounds = new Rectangle(352, 162, 92, 32), DialogResult = DialogResult.Yes };
            var left = TimeoutSeconds;
            var count = new Label { Text = $"No answer in {left} s = Deny", AutoSize = true, Location = new Point(16, 170), ForeColor = SystemColors.GrayText };
            f.Controls.AddRange(new Control[] { title, body, deny, allow, count });
            f.AcceptButton = deny; // Enter must never approve by accident.
            f.CancelButton = deny;
            Ui.Theme.Apply(f);
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) =>
            {
                left--;
                count.Text = $"No answer in {left} s = Deny";
                // The kill switch closes the box too.
                if (left <= 0 || Pause.IsOn) { f.DialogResult = DialogResult.No; f.Close(); }
            };
            f.Shown += (_, _) => { f.Activate(); deny.Focus(); timer.Start(); };
            allowed = f.ShowDialog() == DialogResult.Yes;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(TimeoutSeconds + 5));
        return allowed;
    }
}
