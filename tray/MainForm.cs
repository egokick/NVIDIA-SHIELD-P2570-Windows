using System.Diagnostics;
using System.Text.Json;

namespace ShieldController;

internal sealed class MainForm : Form
{
    private readonly OwnerClient owner = new();
    private readonly StatusModel model = new();
    private readonly NotifyIcon? tray;
    private readonly Icon appIcon = AppIcon.Load();
    private readonly Label stateLabel = new(), description = new(), requirements = new(), activity = new();
    private readonly ChoicePicker wifi = new("Select Wi-Fi adapter"), uplink = new("Select internet adapter"), candidates = new("Search to find a controller");
    private readonly Button connect = new(), pair = new(), refresh = new(), recover = new();
    private readonly List<LinkLabel> dependencyLinks = [];
    private readonly ToolStripMenuItem trayConnect = new("Connect"), trayPair = new("Pair controller…");
    private readonly ToolStripMenuItem trayForget = new("Forget controller…");
    private readonly bool preview;
    private bool busy, exiting, allowClose, automaticConnectAttempted;
    private Capabilities? capabilities;
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 1000 };

    internal MainForm(bool preview = false)
    {
        this.preview = preview;
        Text = "NVIDIA SHIELD P2570 for Windows";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 246);
        ForeColor = Color.FromArgb(27, 36, 28);
        ClientSize = new Size(720, 718);
        MinimumSize = new Size(620, 450);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = !preview;
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildControls();
        if (!preview)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowWindow());
            menu.Items.Add(trayConnect); trayConnect.Click += async (_, _) => await ConnectClick();
            menu.Items.Add(trayPair); trayPair.Click += (_, _) => { ShowWindow(); pair.Focus(); };
            menu.Items.Add("Settings", null, (_, _) => { ShowWindow(); wifi.Focus(); activity.Text = model.Paired ? "To change controllers or network adapters, disconnect and choose Forget controller from the tray menu, then pair again." : "Choose the Wi-Fi and internet adapters before pairing."; });
            menu.Items.Add("Test controller…", null, (_, _) => TestController());
            menu.Items.Add(trayForget); trayForget.Click += async (_, _) => await ForgetClick();
            menu.Items.Add("Open log folder", null, (_, _) => OpenLogs());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
            tray = new NotifyIcon { Icon = appIcon, Text = "SHIELD P2570 · Not paired", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => ShowWindow();
            owner.SessionEvent += e => Ui(() => { model.ApplySession(e); ApplyState(); });
            owner.SessionFailed += () => Ui(() => { model.Fail("The controller service returned an invalid status. Its wireless session is being stopped safely."); ApplyState(); });
            owner.SessionEnded += code => Ui(() => { if (code == 0) model.SetDisconnected(); else model.Fail("The connection stopped. Open logs for details, then refresh to check recovery."); ApplyState(); });
            Shown += async (_, _) => await RefreshCapabilities(autoConnect: true);
            stateTimer.Tick += (_, _) => ApplyState(); stateTimer.Start();
            Load += (_, _) => {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                Location = new Point(Math.Max(work.Left, work.Left + (work.Width - Width) / 2), Math.Max(work.Top, work.Top + (work.Height - Height) / 2));
            };
        }
        FormClosing += (_, e) => { if (!allowClose && !preview) { e.Cancel = true; Hide(); } };
        ApplyState();
    }
    private void Ui(Action action)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(() => { if (!IsDisposed) action(); }); } catch (InvalidOperationException) { }
    }
    private void BuildControls()
    {
        var scroll = new SetupScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(0, 718) };
        var page = new TableLayoutPanel { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Height = 718, Padding = new Padding(24, 20, 24, 16), ColumnCount = 1, RowCount = 8 };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 87));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 109));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        Controls.Add(scroll); scroll.Controls.Add(page);
        scroll.Resize += (_, _) => { page.Width = scroll.ClientSize.Width; requirements.MaximumSize = new Size(Math.Max(200, scroll.ClientSize.Width - 52), 0); };
        page.Width = scroll.ClientSize.Width;
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty };
        heading.Controls.Add(new Label { Text = "NVIDIA SHIELD P2570", Font = new Font("Segoe UI Semibold", 20), AutoSize = true, Margin = Padding.Empty });
        heading.Controls.Add(new Label { Text = "Wireless controller for Windows  ·  Wi-Fi connection", AutoSize = true, ForeColor = Color.FromArgb(89, 101, 91), Margin = Padding.Empty });
        page.Controls.Add(heading);
        var status = new TableLayoutPanel { BackColor = Color.White, Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 10), RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
        stateLabel.AutoSize = true; stateLabel.Font = new Font("Segoe UI Semibold", 16); stateLabel.Margin = Padding.Empty;
        description.Dock = DockStyle.Fill; description.Margin = new Padding(0, 4, 0, 0);
        status.Controls.Add(stateLabel); status.Controls.Add(description); page.Controls.Add(status);
        var req = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = Padding.Empty };
        req.Controls.Add(new Label { Text = "REQUIREMENTS", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty });
        requirements.AutoSize = true; requirements.MaximumSize = new Size(660, 0); requirements.Text = "Checking Windows, Wi-Fi, Npcap and the virtual gamepad driver…"; requirements.Margin = new Padding(0, 3, 0, 3); req.Controls.Add(requirements);
        var links = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        AddDependencyLink(links, "Install Npcap", "Npcap");
        AddDependencyLink(links, "Install ViGEmBus", "ViGEmBus");
        AddLink(links, "Project and help", "https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows");
        req.Controls.Add(links); page.Controls.Add(req);
        var network = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        network.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); network.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); network.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        network.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); network.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        network.Controls.Add(new Label { Text = "NETWORK ADAPTERS", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty }, 0, 0); network.SetColumnSpan(network.GetControlFromPosition(0, 0)!, 2);
        network.Controls.Add(new Label { Text = "Controller Wi-Fi", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        network.Controls.Add(new Label { Text = "Internet connection", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        foreach (var picker in new[] { wifi, uplink, candidates })
        { picker.Dock = DockStyle.Fill; picker.TextAlign = ContentAlignment.MiddleLeft; picker.AutoEllipsis = true; picker.MinimumSize = new Size(120, 29); picker.SelectedIndex = -1; }
        network.Controls.Add(wifi, 1, 1); network.Controls.Add(uplink, 1, 2); page.Controls.Add(network);
        var guide = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Margin = new Padding(0, 9, 0, 7), Padding = new Padding(12, 8, 12, 8), BackColor = Color.FromArgb(231, 237, 230) };
        guide.Controls.Add(new Label { Text = "PAIR YOUR CONTROLLER", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty });
        guide.Controls.Add(new Label { Text = "1. Unplug the USB cable from your PC. A wall USB charger is fine.\n2. Touch and hold the green NVIDIA logo for 3 seconds until it flashes.\n3. Choose Find controller below, then select and pair the controller.", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 5, 0, 3) });
        var help = new LinkLabel { Text = "Logo will not flash?", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        help.LinkClicked += (_, _) => { if (!preview) MessageBox.Show(this, "Charge the controller using a wall USB charger. If it still will not enter pairing mode, touch and hold the NVIDIA logo for 15 seconds to reset it, release, then hold for 3 seconds until it flashes.\n\nAfter pairing, a short tap reconnects it. Do not hold for 3 seconds to reconnect.", "Pairing help", MessageBoxButtons.OK, MessageBoxIcon.Information); };
        guide.Controls.Add(help); guide.Controls.Add(candidates); page.Controls.Add(guide);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        foreach (var b in new[] { connect, pair, refresh, recover }) { b.AutoSize = true; b.MinimumSize = new Size(95, 34); b.Margin = new Padding(0, 3, 8, 3); actions.Controls.Add(b); }
        connect.Text = "Connect"; connect.BackColor = Color.FromArgb(216, 237, 203); connect.FlatStyle = FlatStyle.System;
        pair.Text = "Find controller"; refresh.Text = "Refresh"; recover.Text = "Recover connection";
        connect.Click += async (_, _) => await ConnectClick(); pair.Click += async (_, _) => await PairClick();
        refresh.Click += async (_, _) => await RefreshCapabilities(); recover.Click += async (_, _) => await RecoverClick(); page.Controls.Add(actions);
        activity.Dock = DockStyle.Fill; activity.ForeColor = Color.FromArgb(89, 101, 91); activity.Margin = new Padding(0, 4, 0, 0); page.Controls.Add(activity);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        footer.Controls.Add(new Label { Text = "Gamepad input only · No Bluetooth pairing needed", AutoSize = true, ForeColor = Color.FromArgb(89, 101, 91), Margin = new Padding(0, 2, 18, 0) });
        var log = new LinkLabel { Text = "Open logs", AutoSize = true, Margin = new Padding(0, 2, 0, 0) }; log.LinkClicked += (_, _) => OpenLogs(); footer.Controls.Add(log); page.Controls.Add(footer);
        var test = new LinkLabel { Text = "Test controller", AutoSize = true, Margin = new Padding(18, 2, 0, 0) }; test.LinkClicked += (_, _) => TestController(); footer.Controls.Add(test);
    }
    private void AddLink(Control parent, string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
        link.LinkClicked += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }; parent.Controls.Add(link);
    }
    private void AddDependencyLink(Control parent, string text, string component)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
        link.Tag = component;
        dependencyLinks.Add(link);
        link.LinkClicked += async (_, _) => {
            bool refreshAfter = false;
            await Work(async () => {
                if (owner.Running || owner.CommandPending) throw new InvalidOperationException("Disconnect before installing a driver.");
                string helper = Path.Combine(OwnerClient.Root, "bin", "Install-Dependencies.ps1");
                if (!File.Exists(helper)) throw new FileNotFoundException("The dependency installer is missing.");
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Component", component }) start.ArgumentList.Add(arg);
                activity.Text = "Complete the official driver installer prompts, then return here.";
                using var process = Process.Start(start) ?? throw new IOException("The driver installer could not start.");
                await process.WaitForExitAsync();
                if (process.ExitCode == 3010) { activity.Text = "Driver installed. Restart Windows before connecting the controller."; return; }
                if (process.ExitCode != 0) throw new InvalidOperationException("The driver installer did not complete.");
                refreshAfter = true;
            });
            if (refreshAfter) await RefreshCapabilities();
        };
        parent.Controls.Add(link);
    }
    private void OpenLogs() { if (!preview && Directory.Exists(owner.LogDirectory)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { owner.LogDirectory } }); }
    private void TestController() { if (!preview) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe")) { UseShellExecute = true, ArgumentList = { "joy.cpl" } }); }
    private void ApplyState()
    {
        stateLabel.Text = StatusModel.Title(model.State); description.Text = model.Message;
        stateLabel.ForeColor = model.State == ControllerState.Connected ? Color.FromArgb(37, 113, 31) : model.State == ControllerState.Error ? Color.FromArgb(159, 51, 34) : ForeColor;
        if (tray is not null) tray.Text = "SHIELD P2570 · " + StatusModel.Title(model.State);
        bool running = owner.Running || preview && model.State == ControllerState.Connected;
        connect.Text = trayConnect.Text = running ? "Disconnect" : "Connect";
        connect.Enabled = trayConnect.Enabled = !busy && !owner.CommandPending && (running || model.Paired && capabilities?.Requirements.All(r => r.Available) == true && capabilities.RecoveryRequired == false);
        pair.Enabled = trayPair.Enabled = !busy && !running && !owner.CommandPending && capabilities is not null && capabilities.Requirements.All(r => r.Available) && !capabilities.RecoveryRequired;
        trayForget.Enabled = !busy && !running && !owner.CommandPending && capabilities is not null && !capabilities.RecoveryRequired;
        refresh.Enabled = !busy && !running && !owner.CommandPending;
        wifi.Enabled = uplink.Enabled = !busy && !running && !model.Paired;
        candidates.Enabled = !busy && !running && candidates.Items.Count > 0;
        recover.Visible = capabilities?.RecoveryRequired == true; recover.Enabled = !busy && !running && !owner.CommandPending;
        foreach (var link in dependencyLinks)
        {
            string component = (string)link.Tag!;
            string id = component == "Npcap" ? "npcap" : "vigem";
            bool installed = capabilities?.Requirements.Any(r => r.Id == id && r.Available) == true;
            link.Text = installed ? component + " installed" : "Install " + component;
            link.Enabled = !installed && !busy && !running && !owner.CommandPending;
        }
        pair.Text = candidates.Items.Count > 0 ? "Pair selected controller" : "Find controller";
    }
    private async Task Work(Func<Task> operation)
    {
        if (preview || busy || exiting) return;
        busy = true; ApplyState();
        try { await operation(); }
        catch (Exception e) { model.Fail(e is TimeoutException ? e.Message : "The operation did not complete. Open logs for details, then refresh to check setup."); activity.Text = e is FileNotFoundException || !File.Exists(OwnerClient.Executable) ? "The controller service is missing. Reinstall the application." : "No keyboard or mouse input was sent."; }
        finally { busy = false; ApplyState(); }
    }
    private async Task RefreshCapabilities(bool autoConnect = false)
    {
        await Work(async () => {
            activity.Text = "Checking requirements…";
            capabilities = await owner.GetCapabilities(); if (exiting) return; model.ApplyCapabilities(capabilities);
            requirements.Text = RequirementSummary(capabilities);
            SetAdapters(wifi, capabilities.Wifi, capabilities.SelectedWifi); SetAdapters(uplink, capabilities.Uplink, capabilities.SelectedUplink);
            activity.Text = capabilities.Requirements.All(r => r.Available) ? "Keep this app running in the tray while you play. Closing this window keeps it running." : "Required: " + capabilities.Requirements.First(r => !r.Available).Label + ". Complete setup, then select Refresh.";
        });
        if (autoConnect && !automaticConnectAttempted && model.Paired && model.State != ControllerState.Error && capabilities?.Requirements.All(r => r.Available) == true)
        { automaticConnectAttempted = true; await ConnectClick(); }
    }
    internal static string RequirementSummary(Capabilities c) => c.Requirements.All(r => r.Available)
        ? "✓ Windows 11   ·   ✓ Wi-Fi   ·   ✓ Npcap   ·   ✓ ViGEmBus"
        : "Needs setup: " + string.Join(", ", c.Requirements.Where(r => !r.Available).Select(r => r.Id switch { "windows" => "Windows version", "npcap" => "Npcap", "vigem" => "ViGEmBus", "package" => "application files", "wifi" => "Wi-Fi access", "pairing-radio" => "Wi-Fi selection", "uplink" => "internet connection", _ => "connection requirements" }));
    private static void SetAdapters(ChoicePicker control, AdapterChoice[] values, string? selected)
    {
        control.SetItems(values);
        int i = Array.FindIndex(values, x => string.Equals(x.Guid, selected, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) control.SelectedIndex = i; else if (values.Length == 1) control.SelectedIndex = 0;
    }
    private async Task ConnectClick() => await Work(async () => {
        if (owner.Running) { activity.Text = "Disconnecting and restoring network settings…"; await owner.StopSession(); model.SetDisconnected(); activity.Text = "Disconnected. Your wireless session has been stopped."; }
        else { model.SetDisconnected("Starting the wireless connection…"); owner.StartSession(); activity.Text = "Tap the NVIDIA logo once, then move a stick. A real input report confirms connection."; }
    });
    private async Task PairClick() => await Work(async () => {
        if (wifi.SelectedItem is not AdapterChoice w || uplink.SelectedItem is not AdapterChoice u) { activity.Text = "Select your Wi-Fi and internet adapters first."; return; }
        model.BeginPairing(); ApplyState();
        if (candidates.SelectedItem is Candidate target)
        {
            activity.Text = "Pairing the selected controller. Keep its green logo flashing and wait for confirmation.";
            await owner.Pair(target, w.Guid, u.Guid); if (exiting) return; model.PairSucceeded(); candidates.SetItems([]);
            capabilities = await owner.GetCapabilities(); model.ApplyCapabilities(capabilities);
            activity.Text = "Pairing complete. Select Connect, then tap the NVIDIA logo once to reconnect.";
        }
        else
        {
            activity.Text = "Searching for 20 seconds. Keep the logo flashing. No controller is paired automatically.";
            var found = await owner.Discover(w.Guid, u.Guid); if (exiting) return; candidates.SetItems(found);
            if (found.Length > 0) candidates.SelectedIndex = 0;
            model.SetDisconnected(found.Length > 0 ? "Controller found. Select it below and choose Pair selected controller." : "No controller found. Charge it from a wall charger, make the logo flash, then try again.");
            activity.Text = found.Length > 0 ? "Only the controller you select will be paired." : "The P2570 uses Wi-Fi. It will not appear in Bluetooth pairing.";
        }
    });
    private async Task RecoverClick() { bool recovered = false; await Work(async () => { activity.Text = "Restoring the previous owned wireless session…"; await owner.Recover(); recovered = true; }); if (recovered) await RefreshCapabilities(); }
    private async Task ForgetClick()
    {
        if (preview || busy || owner.Running || owner.CommandPending || capabilities?.RecoveryRequired != false) return;
        ShowWindow();
        if (MessageBox.Show(this, "Remove this app's saved pairing from this PC?\n\nYou will need to pair again before connecting. This does not reset the controller itself.", "Forget controller", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool forgotten = false;
        await Work(async () => { await owner.Forget(); forgotten = true; candidates.SetItems([]); });
        if (forgotten && !exiting) await RefreshCapabilities();
    }
    internal void ShowWindow() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    internal async Task ExitAsync()
    {
        if (exiting || preview) return;
        if (busy && !owner.Running && !owner.CommandPending)
        { ShowWindow(); activity.Text = "Finish the current installer or setup check before exiting."; return; }
        exiting = true; ShowWindow(); Enabled = false; activity.Text = "Stopping the controller and restoring network settings…";
        try { await owner.StopSession(); allowClose = true; tray!.Visible = false; Close(); }
        catch { Enabled = true; exiting = false; model.Fail("Cleanup is still in progress. Wait, then try Exit again. The application will stay open to finish safely."); ApplyState(); }
    }
    internal void RenderPreview(ControllerState state, string path, Size? viewport = null)
    {
        if (viewport is Size size) Size = size;
        capabilities = new(state is not ControllerState.NotPaired and not ControllerState.Pairing, false,
            [new("windows", "Windows 11 24H2 (build 26100) or later", true), new("npcap", "Npcap installed with non-administrator capture enabled", state != ControllerState.Error),
             new("vigem", "ViGEmBus virtual Xbox controller driver", true), new("package", "Installed application files match their package manifest", true),
             new("wifi", "Wi-Fi adapter available; Windows Location access may be required", true), new("pairing-radio", "One enabled Wi-Fi adapter during first pairing", true),
             new("uplink", "Active Internet connection required by the current Windows Mobile Hotspot path", true), new("channel", "A 5 GHz Wi-Fi Direct group on channel 36 is required (verified at Start)", true)],
            [new("preview", "Realtek 8852CE Wi-Fi 6E PCI-E NIC")], [new("preview", "Ethernet")], "preview", "preview");
        model.ApplyCapabilities(capabilities);
        requirements.Text = RequirementSummary(capabilities);
        SetAdapters(wifi, capabilities.Wifi, "preview"); SetAdapters(uplink, capabilities.Uplink, "preview");
        if (state == ControllerState.Pairing) { model.BeginPairing(); busy = true; activity.Text = "Searching for 20 seconds. Keep the logo flashing. No controller is paired automatically."; }
        else if (state == ControllerState.Connected) { using var e = JsonDocument.Parse("{\"connected\":true,\"transport_ready\":true}"); model.ApplySession(e.RootElement); activity.Text = "Keep this app running in the tray while you play."; }
        else if (state == ControllerState.Error) { model.Fail("Npcap is required before connecting. Install it below, then select Refresh."); activity.Text = "Required: " + capabilities.Requirements.First(r => !r.Available).Label + ". Complete setup, then select Refresh."; }
        else activity.Text = "Keep this app running in the tray while you play. Closing this window keeps it running.";
        ApplyState();
        // Create HWNDs without showing the top-level window. WM_PRINT then draws
        // the same native controls used by the tray UI into an owned bitmap.
        static void PrepareControl(Control c) { _ = c.Handle; c.PerformLayout(); foreach (Control child in c.Controls) PrepareControl(child); }
        PrepareControl(this); PerformLayout();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        string? scrolledPath = null;
        if (viewport is not null && Controls[0] is SetupScrollPanel scroll && scroll.ClientSize.Height < scroll.AutoScrollMinSize.Height)
        {
            scroll.PerformLayout();
            scroll.ScrollToBottom();
            scrolledPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-scrolled.png");
            using var bottom = new Bitmap(Width, Height); DrawToBitmap(bottom, new Rectangle(Point.Empty, Size)); bottom.Save(scrolledPath);
        }
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new { state = state.ToString(), capture = "WinForms DrawToBitmap of production controls", visible = Visible, show_in_taskbar = ShowInTaskbar,
            owner_or_radio_calls = 0, dimensions = new { width = bitmap.Width, height = bitmap.Height }, control_count = Controls.Count,
            selected_wifi_displayed = wifi.SelectedItem?.ToString(), selected_uplink_displayed = uplink.SelectedItem?.ToString(), connection_action = connect.Text,
            pair_action_enabled = pair.Enabled, wifi_selection_enabled = wifi.Enabled, uplink_selection_enabled = uplink.Enabled, requirement_count = capabilities.Requirements.Length,
            scroll_position = ((Panel)Controls[0]).AutoScrollPosition.ToString(), scroll_display = ((Panel)Controls[0]).DisplayRectangle.ToString(), scroll_maximum = ((Panel)Controls[0]).VerticalScroll.Maximum,
            scrolled_png_sha256 = scrolledPath is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(scrolledPath))),
            png_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }));
    }
    protected override void Dispose(bool disposing) { if (disposing) { stateTimer.Dispose(); tray?.Dispose(); owner.Dispose(); } base.Dispose(disposing); if (disposing) appIcon.Dispose(); }
}
