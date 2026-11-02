using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;

internal sealed class SetupForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 49, 42), Muted = Color.FromArgb(86, 104, 96), Green = Color.FromArgb(166, 228, 61), Background = Color.FromArgb(247, 249, 244);
    private readonly Label title = new(), message = new(), footer = new(), steps = new();
    private readonly ProgressBar progress = new();
    private readonly Button primary = new InstallButton(), settings = new();
    private readonly LinkLabel appLink = new();
    private readonly SetupFlow flow;
    private readonly bool preview, launch;
    private readonly Image? artwork;
    private readonly Icon? appIcon;
    private SetupView view = new(SetupStage.Welcome, "Let's get you playing", "We'll install the app and set up its drivers.\nKeep Wi-Fi and your internet connection on.");
    private bool busy;
    private Action? layoutPage;
    private bool layingOut;

    internal SetupForm(ISetupOperations operations, bool launch, bool preview = false)
    {
        this.preview = preview; this.launch = launch; flow = new(operations);
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "SHIELD Controller · Setup"; ClientSize = new Size(560, 580); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = Background; ForeColor = Ink;
        Font = new Font("Segoe UI", 11); ShowInTaskbar = !preview;
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); Icon = appIcon;
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("controller.png")) if (resource is not null) { using var image = Image.FromStream(resource); artwork = new Bitmap(image); }
        var picture = new PictureBox { Image = artwork, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 8), AccessibleName = "SHIELD game controller" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(34, 24, 34, 24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 24, 108, 52, 92, 32, 16, 54, 46, 34, 24 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var brand = new Label { Text = "SHIELD  /  P2570", Font = new Font("Segoe UI Semibold", 11), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        title.Font = new Font("Segoe UI Semibold", 25); title.TextAlign = ContentAlignment.MiddleCenter;
        message.Font = new Font("Segoe UI", 11); message.ForeColor = Muted; message.TextAlign = ContentAlignment.MiddleCenter;
        steps.Font = new Font("Segoe UI Semibold", 10); steps.ForeColor = Muted; steps.TextAlign = ContentAlignment.MiddleCenter;
        footer.Font = new Font("Segoe UI", 9); footer.ForeColor = Muted; footer.TextAlign = ContentAlignment.MiddleCenter;
        foreach (var label in new[] { title, message, steps, footer }) { label.Dock = DockStyle.Fill; label.UseMnemonic = false; label.Margin = Padding.Empty; }
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(3, 3, 3, 6); progress.Style = ProgressBarStyle.Continuous;
        primary.Dock = DockStyle.Fill; primary.Margin = new Padding(0, 7, 0, 1); primary.AccessibleName = "Install and set up controller";
        primary.Click += async (_, _) => await Act(); AcceptButton = primary;
        settings.Dock = DockStyle.Fill; settings.FlatStyle = FlatStyle.Flat; settings.FlatAppearance.BorderSize = 0; settings.ForeColor = Ink; settings.BackColor = Background; settings.Text = "Open Windows Settings";
        settings.Click += (_, _) => { if (!preview) OpenSettings(uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })); };
        appLink.Text = "Open SHIELD Controller"; appLink.AutoSize = false; appLink.Dock = DockStyle.Fill; appLink.TextAlign = ContentAlignment.MiddleCenter; appLink.LinkColor = Muted;
        appLink.LinkClicked += (_, _) => {
            if (preview) return;
            try { Program.Launch(); }
            catch (Exception error) { Program.ReportSetupError(error); Apply(new(SetupStage.Error, "The app could not open", "Your app is installed. Open SHIELD Controller from Start, or click Try again to repair its files.")); }
        };
        layout.Controls.Add(brand, 0, 0); layout.Controls.Add(picture, 0, 1); layout.Controls.Add(title, 0, 2); layout.Controls.Add(message, 0, 3);
        layout.Controls.Add(steps, 0, 4); layout.Controls.Add(progress, 0, 5); layout.Controls.Add(primary, 0, 6); layout.Controls.Add(settings, 0, 7); layout.Controls.Add(appLink, 0, 8); layout.Controls.Add(footer, 0, 9);
        var scroll = new ShieldController.SetupScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
        layout.Dock = DockStyle.None;
        layoutPage = () => {
            if (layingOut) return;
            layingOut = true;
            try {
                int S(int value) => (int)Math.Round(value * DeviceDpi / 96f);
                layout.Padding = new Padding(S(34), S(24), S(34), S(24));
                int width = scroll.ClientSize.Width, textWidth = Math.Max(1, width - layout.Padding.Horizontal);
                int[] heights = [24, 108, 52, 92, 32, 16, 54, 46, 34, 24];
                foreach (var (label, row) in new[] { (brand, 0), (title, 2), (message, 3), (steps, 4), (footer, 9) })
                    heights[row] = Math.Max(heights[row], (int)Math.Ceiling(TextRenderer.MeasureText(label.Text, label.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height * 96f / DeviceDpi) + 12);
                for (int row = 0; row < heights.Length; row++) layout.RowStyles[row].Height = S(heights[row]);
                layout.Size = new Size(width, heights.Sum(S) + layout.Padding.Vertical);
            }
            finally { layingOut = false; }
        };
        scroll.Controls.Add(layout); Controls.Add(scroll);
        scroll.ClientSizeChanged += (_, _) => layoutPage();
        Load += (_, _) => { var work = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height)); };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Apply(view);
        ResumeLayout(true);
        layoutPage();
    }
    private async Task Act()
    {
        if (preview || busy) return;
        if (view.Stage == SetupStage.Ready) { LaunchAndClose(); return; }
        if (view.Stage == SetupStage.Restart) { DialogResult = DialogResult.Retry; Close(); return; }
        if (view.Stage == SetupStage.RestartRetry) { DialogResult = DialogResult.Ignore; Close(); return; }
        busy = true;
        try { Apply(await flow.Run(Apply)); }
        finally { busy = false; Apply(view); }
        if (view.Stage == SetupStage.Ready && launch) LaunchAndClose();
    }
    private void LaunchAndClose()
    {
        try { if (launch) Program.Launch(); DialogResult = DialogResult.OK; Close(); }
        catch (Exception error) { Program.ReportSetupError(error); Apply(new(SetupStage.Error, "The app could not open", "Your app and drivers are installed. Open SHIELD Controller from Start, or click Try again to repair its files.")); }
    }
    internal SetupView CurrentView => view;
    internal void OpenSettings(Action<string> open)
    {
        if (view.SettingsUri is not string uri) return;
        try { open(uri); }
        catch (Exception error) {
            Program.ReportSetupError(error);
            string instructions = uri switch {
                "ms-settings:privacy-location" => "Press Windows + I. Open Privacy & security → Location, turn on Location services and desktop app access, then click Check again.",
                "ms-settings:network-wifi" => "Press Windows + I. Open Network & internet → Wi-Fi and turn Wi-Fi on. Then click Check again.",
                "ms-settings:network-advancedsettings" => "Press Windows + I. Open Network & internet → Advanced network settings. Keep one Wi-Fi adapter enabled, then click Check again.",
                "ms-settings:network-status" => "Press Windows + I. Open Network & internet and connect this PC to the internet. Then click Check again.",
                "ms-settings:windowsupdate" => "Press Windows + I. Open Windows Update and install the latest Windows version, then run SHIELD Setup again.",
                _ => "Open Windows Settings from Start, make the requested change, then click Check again."
            };
            Apply(view with { Stage = SetupStage.SettingsHelp, Title = "Open Settings from Start", Message = instructions });
        }
    }
    internal void Apply(SetupView next)
    {
        view = next; title.Text = next.Title; message.Text = next.Message; progress.Value = Math.Clamp(next.Percent, 0, 100);
        bool active = next.Stage is SetupStage.Checking or SetupStage.Installing or SetupStage.Downloading or SetupStage.ConnectionTool or SetupStage.GamepadDriver or SetupStage.Verifying;
        progress.Visible = active; primary.Enabled = !active && !busy; settings.Visible = next.SettingsUri is not null;
        appLink.Visible = (flow.AppInstalled || preview) && next.Stage is SetupStage.Attention or SetupStage.SettingsHelp or SetupStage.Error;
        primary.Text = next.Stage switch { SetupStage.Welcome => "Install & play", SetupStage.Ready => launch ? "Let's pair" : "Done", SetupStage.Restart or SetupStage.RestartRetry => "Close · I'll restart later", SetupStage.Attention or SetupStage.SettingsHelp => "Check again", SetupStage.Error => "Try again", _ => "Setting things up…" };
        steps.Text = next.Stage switch { SetupStage.Welcome => "1   App        2   Drivers        3   Pair & play", SetupStage.Ready => "✓   App ready      ✓   Drivers ready", SetupStage.Restart or SetupStage.RestartRetry => "✓   App installed      ↻   Restart needed", SetupStage.Attention or SetupStage.SettingsHelp => "✓   App installed      ✓   Drivers ready", _ => "App  →  Drivers  →  Pair & play" };
        footer.Text = next.Stage == SetupStage.Welcome ? "Free · No account · Windows 11 x64" : active ? "Keep this window open. We will continue automatically." : "Your pairing and custom controls are kept.";
        layoutPage?.Invoke();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); layoutPage?.Invoke(); }
    internal void Render(string path, SetupStage scene, Size? viewport = null)
    {
        if (!preview) throw new InvalidOperationException("Preview mode is required.");
        if (viewport is Size size) Size = size;
        var sample = scene switch {
            SetupStage.Welcome => view,
            SetupStage.ConnectionTool => new(SetupStage.ConnectionTool, "One quick approval", "In the Npcap window, click I Agree, Install, then Finish. The correct options are already selected.", 55),
            SetupStage.GamepadDriver => new(SetupStage.GamepadDriver, "Your gamepad driver", "Click Yes if Windows asks. The gamepad driver installs automatically.", 75),
            SetupStage.Ready => new(SetupStage.Ready, "Ready to pair!", "Unplug your controller from the PC. The app will show you which button to hold.", 100),
            SetupStage.Restart => new(SetupStage.Restart, "One restart, then play", "Restart Windows when you are ready. Then open SHIELD Controller from Start.", 100),
            SetupStage.RestartRetry => new(SetupStage.RestartRetry, "Restart, then try Setup again", "Restart Windows, then run SHIELD Setup again to finish the connection tool. Your app is installed.", 100),
            SetupStage.Attention or SetupStage.SettingsHelp => new(SetupStage.Attention, "Allow Wi-Fi access", "In Windows Settings, turn on Location services and let desktop apps access your location. Then click Check again.", 100, "ms-settings:privacy-location"),
            SetupStage.Error => new(SetupStage.Error, "Let's finish your setup", "Windows approval was cancelled. Click Try again, then click Yes when Windows asks."),
            _ => new(scene, "Getting everything ready", "We are checking the app and official drivers…", 50)
        };
        Apply(sample);
        if (scene == SetupStage.SettingsHelp) OpenSettings(_ => throw new System.ComponentModel.Win32Exception(2));
        static void Layout(Control c) { _ = c.Handle; c.PerformLayout(); foreach (Control child in c.Controls) Layout(child); }
        Layout(this); PerformLayout(); Layout(this);
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        var scroll = Controls.OfType<ShieldController.SetupScrollPanel>().Single();
        bool scrolled = scroll.VerticalScroll.Visible;
        if (scrolled)
        {
            scroll.ScrollToBottom();
            using var bottom = new Bitmap(Width, Height); DrawToBitmap(bottom, new Rectangle(Point.Empty, Size));
            bottom.Save(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-scrolled.png"));
        }
        bool textFits = new[] { title, message, steps, footer }.All(label => TextRenderer.MeasureText(label.Text, label.Font, new Size(label.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height <= label.ClientSize.Height);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new { scene, visible = Visible, show_in_taskbar = ShowInTaskbar, hardware_calls = 0, capture = "production installer controls, hidden DrawToBitmap", width = Width, height = Height, dpi = DeviceDpi, text_fits = textFits, horizontal_scroll = scroll.HorizontalScroll.Visible, scrolled_capture = scrolled }));
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { artwork?.Dispose(); appIcon?.Dispose(); } }
    private sealed class InstallButton : Button
    {
        internal InstallButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Font = new Font("Segoe UI Semibold", 13); Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath(); var r = new Rectangle(0, 0, Width - 1, Height - 1); int d = 22;
            path.AddArc(r.X,r.Y,d,d,180,90); path.AddArc(r.Right-d,r.Y,d,d,270,90); path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.X,r.Bottom-d,d,d,90,90); path.CloseFigure();
            using var brush = new SolidBrush(Enabled ? Green : Color.FromArgb(227, 233, 221)); e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6));
        }
    }
}
