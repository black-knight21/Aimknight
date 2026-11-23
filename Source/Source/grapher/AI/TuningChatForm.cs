using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Models.Theming;
using grapher.Models.Theming.Controls;
using Newtonsoft.Json.Linq;

namespace grapher.AI
{
    internal sealed class TuningChatForm : Form
    {
        private readonly AccelGUI gui;
        private readonly WriterBridge bridge = new WriterBridge();
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly TextBox key = new ThemeableTextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly TextBox model = new ThemeableTextBox { Dock = DockStyle.Fill };
        private readonly TextBox prompt = new ThemeableTextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox preview = new ThemeableTextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false };
        private readonly Button send = new Button { Text = "Generate proposal", AutoSize = true };
        private readonly Button apply = new Button { Text = "Apply proposal", AutoSize = true, Enabled = false };
        private readonly Button restore = new Button { Text = "Restore previous", AutoSize = true };
        private readonly Label status = new Label { AutoSize = true, Text = "Ready. Changes apply only when you click Apply proposal." };
        private JObject baseline, proposal;
        private bool busy;

        public TuningChatForm(AccelGUI gui)
        {
            this.gui = gui;
            Font = new Font("Segoe UI", 9F);
            Text = "Kaiden Accel — AI tuning";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Size = new Size(850, 750);
            MinimumSize = new Size(720, 600);
            StartPosition = FormStartPosition.CenterParent;
            key.Text = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            model.Text = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.8-flash";
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            float[] heights = { 32, 32, 64, 90, 38, 0, 38, 40 };
            for (int i = 0; i < heights.Length; i++) layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.Absolute, i == 5 ? 100 : heights[i]));
            layout.Controls.Add(new Label { Text = "Gemini key", AutoSize = true }, 0, 0);
            layout.Controls.Add(key, 1, 0);
            layout.Controls.Add(new Label { Text = "Model ID", AutoSize = true }, 0, 1);
            layout.Controls.Add(model, 1, 1);
            var notice = new Label { Dock = DockStyle.Fill, Text = "Generate sends your request, the first/default profile, and device defaults to Google Gemini. Keys stay in memory. Each request starts from the live driver settings. Other profiles and device assignments stay local." };
            layout.Controls.Add(notice, 0, 2); layout.SetColumnSpan(notice, 2);
            layout.Controls.Add(new Label { Text = "Request", AutoSize = true }, 0, 3);
            layout.Controls.Add(prompt, 1, 3);
            layout.Controls.Add(send, 1, 4);
            layout.Controls.Add(preview, 0, 5); layout.SetColumnSpan(preview, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            actions.Controls.Add(apply); actions.Controls.Add(restore);
            layout.Controls.Add(actions, 0, 6); layout.SetColumnSpan(actions, 2);
            layout.Controls.Add(status, 0, 7); layout.SetColumnSpan(status, 2);
            Controls.Add(layout);
            Theme.Apply(this);
            send.Click += async (s, e) => await RunAsync(GenerateAsync);
            apply.Click += async (s, e) => await RunAsync(() => ActivateAsync(false));
            restore.Click += async (s, e) => await RunAsync(() => ActivateAsync(true));
            prompt.TextChanged += (s, e) => InvalidateProposal();
            model.TextChanged += (s, e) => InvalidateProposal();
            restore.Enabled = File.Exists(bridge.BackupPath);
            FormClosing += (s, e) => { if (busy) e.Cancel = true; else cancel.Cancel(); };
            FormClosed += (s, e) => { key.Clear(); cancel.Dispose(); };
        }

        private void InvalidateProposal() { proposal = null; apply.Enabled = false; }

        private async Task RunAsync(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            send.Enabled = apply.Enabled = restore.Enabled = prompt.Enabled = model.Enabled = key.Enabled = false;
            try { await action(); }
            catch (Exception ex) { status.Text = "Operation failed. See message."; MessageBox.Show(this, ex.Message, "Kaiden Accel"); }
            finally {
                busy = false; send.Enabled = prompt.Enabled = model.Enabled = key.Enabled = true;
                apply.Enabled = proposal != null;
                restore.Enabled = File.Exists(bridge.BackupPath);
            }
        }

        private async Task GenerateAsync()
        {
            InvalidateProposal();
            baseline = JObject.Parse(DriverConfig.GetActive().ToJSON());
            status.Text = "Asking Gemini…";
            preview.Clear();
            proposal = await new GeminiTuner().ProposeAsync(key.Text, model.Text, prompt.Text, baseline, cancel.Token);
            string diff = TuningProposal.Diff(baseline, proposal);
            if (diff.Length == 0) { proposal = null; status.Text = "Gemini proposed no changes."; return; }
            preview.Text = "PROPOSED CHANGES\r\n\r\n" + diff + "\r\nFULL SETTINGS\r\n\r\n" + proposal;
            status.Text = "Validated. Review the changes, then click Apply proposal.";
        }

        private async Task ActivateAsync(bool undo)
        {
            var expected = undo ? JObject.Parse(DriverConfig.GetActive().ToJSON()) : baseline;
            var next = undo ? TuningProposal.Parse(File.ReadAllText(bridge.BackupPath)) : proposal;
            status.Text = undo ? "Restoring previous settings…" : "Applying settings…";
            await bridge.ApplyAsync(expected, next, !undo);
            InvalidateProposal();
            // The writer has already activated; adopt it without activating a second time.
            try { gui.Settings.AdoptExternalConfig(DriverConfig.GetActive()); }
            catch (Exception ex) {
                throw new InvalidOperationException("The driver was updated, but refreshing or saving the settings failed. Close and reopen the GUI before making further changes. " + ex.Message, ex);
            }
            gui.RefreshActive();
            gui.Settings.SetActiveHandles();
            preview.Text = undo ? "Previous settings restored." : "Proposal applied successfully.";
            status.Text = "Driver updated. Backup: " + bridge.BackupPath;
        }
    }
}
