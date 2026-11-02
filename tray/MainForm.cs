using System.Diagnostics;
using System.Text.Json;

namespace ShieldController;

internal enum PreviewScene { NotPaired, Pairing, Provisioning, PairedDisconnected, Connected, Error, MissingDriver, Recovery, MultipleControllers, Settings, Help, NoController, Connecting, RestartRequired, InstallFailed, NetworkChoice }

internal sealed class MainForm : Form
{
    private readonly OwnerClient owner = new();
    private readonly StatusModel model = new();
    private readonly NotifyIcon? tray;
    private readonly Icon appIcon = AppIcon.Load();
    private readonly Label headline = new(), instruction = new(), badge = new(), hint = new(), requirements = new(), detail = new();
    private readonly ControllerIllustration illustration = new();
    private readonly ActionButton primary = new();
    private readonly Font titleFont = new("Segoe UI Semibold", 27), compactTitleFont = new("Segoe UI Semibold", 23);
    private readonly ChoicePicker wifi = new("Choose Wi-Fi"), uplink = new("Choose internet connection"), candidates = new("Choose your controller");
    private readonly Button disconnect = new(), refresh = new(), forget = new();
    private readonly ToolStripMenuItem trayConnect = new("Connect"), trayPair = new("Pair controller…"), trayForget = new("Forget controller…");
    private readonly SetupScrollPanel home = new(), settings = new(), help = new();
    private readonly TableLayoutPanel page = new();
    private readonly bool preview;
    private bool busy, exiting, allowClose, automaticConnectAttempted, searchEmpty, previewRunning, restartRequired;
    private TaskCompletionSource? activeWork;
    private string progress = "Checking";
    private Capabilities? capabilities;
    private NextAction nextAction;
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 1000 };
    private enum NextAction { None, Pair, Connect, Hide, Retry, Recover, InstallNpcap, InstallVigem, Settings }
    private bool Running => owner.Running || previewRunning;
    private bool Ready => !restartRequired && capabilities is not null && capabilities.Requirements.Length > 0 && capabilities.Requirements.All(r => r.Available) && !capabilities.RecoveryRequired;

    internal MainForm(bool preview = false)
    {
        this.preview = preview;
        Text = "SHIELD Controller";
        Font = new Font("Segoe UI", 11);
        BackColor = Palette.Background; ForeColor = Palette.Ink;
        ClientSize = new Size(520, 650); MinimumSize = new Size(440, 480);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = !preview; Icon = appIcon; AutoScaleMode = AutoScaleMode.Dpi;
        BuildHome(); BuildSettings(); BuildHelp(); ShowPage(home);
        if (!preview)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowWindow());
            menu.Items.Add(trayConnect); trayConnect.Click += async (_, _) => await ConnectClick();
            menu.Items.Add(trayPair); trayPair.Click += (_, _) => { ShowPage(home); ShowWindow(); primary.Focus(); };
            menu.Items.Add("Settings…", null, (_, _) => { ShowPage(settings); ShowWindow(); });
            menu.Items.Add("Test controller…", null, (_, _) => TestController());
            menu.Items.Add(trayForget); trayForget.Click += async (_, _) => await ForgetClick();
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
            tray = new NotifyIcon { Icon = appIcon, Text = "SHIELD · Not paired", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            owner.SessionEvent += e => Ui(() => { model.ApplySession(e); ApplyState(); });
            owner.SessionFailed += () => Ui(() => { model.Fail("The connection needs another try."); ApplyState(); });
            owner.SessionEnded += code => Ui(() => { if (code == 0) model.SetDisconnected(); else model.Fail("We couldn't connect. Try again."); ApplyState(); });
            Shown += async (_, _) => await RefreshCapabilities(autoConnect: true);
            stateTimer.Tick += (_, _) => ApplyState(); stateTimer.Start();
            Load += (_, _) => {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
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
    private static Label TextLabel(string text, float size = 11, bool bold = false) => new() {
        Text = text, AutoSize = false, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), ForeColor = Palette.Ink,
        TextAlign = ContentAlignment.MiddleLeft };
    private static LinkLabel Link(string text, Action click) {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Palette.Muted, ActiveLinkColor = Palette.Ink,
            VisitedLinkColor = Palette.Muted, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 10, 22, 0), AccessibleName = text, UseMnemonic = false };
        link.LinkClicked += (_, _) => click(); return link;
    }
    private void PreparePage(SetupScrollPanel surface, Control contents, int minimumHeight)
    {
        surface.Dock = DockStyle.Fill; surface.AutoScroll = true;
        surface.AutoScrollMinSize = new Size(0, minimumHeight); surface.BackColor = BackColor;
        contents.Margin = Padding.Empty; contents.Location = Point.Empty; surface.Controls.Add(contents); Controls.Add(surface);
        void ResizeContent() { contents.Size = new Size(surface.ClientSize.Width, Math.Max(surface.ClientSize.Height, (int)(minimumHeight * DeviceDpi / 96f))); }
        surface.Resize += (_, _) => ResizeContent(); ResizeContent();
    }
    private void BuildHome()
    {
        page.ColumnCount = 1; page.RowCount = 10; page.Padding = new Padding(32, 20, 32, 16);
        foreach (float height in new[] { 48f, 32, 50, 54, -1, 0, 58, 32, 40, 0 })
            page.RowStyles.Add(new RowStyle(height == -1 ? SizeType.Percent : SizeType.Absolute, height == -1 ? 100 : height));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        header.Controls.Add(TextLabel("SHIELD  /  P2570", 12, true), 0, 0);
        var settingsLink = Link("Settings", () => ShowPage(settings)); settingsLink.Anchor = AnchorStyles.Right; settingsLink.Margin = Padding.Empty;
        header.Controls.Add(settingsLink, 1, 0); page.Controls.Add(header, 0, 0);
        badge.Dock = DockStyle.Fill; badge.Margin = Padding.Empty; badge.Font = new Font("Segoe UI Semibold", 10); badge.ForeColor = Palette.Muted; badge.TextAlign = ContentAlignment.MiddleLeft; page.Controls.Add(badge, 0, 1);
        headline.Dock = DockStyle.Fill; headline.Margin = Padding.Empty; headline.Font = titleFont; headline.UseMnemonic = false; headline.TextAlign = ContentAlignment.MiddleLeft; page.Controls.Add(headline, 0, 2);
        instruction.Dock = DockStyle.Fill; instruction.Margin = new Padding(0, 3, 0, 0); instruction.Font = new Font("Segoe UI", 12); instruction.ForeColor = Palette.Muted; page.Controls.Add(instruction, 0, 3);
        illustration.Dock = DockStyle.Fill; illustration.Margin = new Padding(0, 2, 0, 8); page.Controls.Add(illustration, 0, 4);
        StylePicker(candidates); candidates.Margin = new Padding(0, 0, 0, 10); candidates.SelectedIndexChanged += (_, _) => ApplyState(); page.Controls.Add(candidates, 0, 5);
        primary.Dock = DockStyle.Fill; primary.Margin = Padding.Empty; primary.Click += async (_, _) => await PrimaryClick(); page.Controls.Add(primary, 0, 6); AcceptButton = primary;
        hint.Dock = DockStyle.Fill; hint.Margin = Padding.Empty; hint.ForeColor = Palette.Muted; hint.Font = new Font("Segoe UI", 9); hint.TextAlign = ContentAlignment.MiddleCenter; page.Controls.Add(hint, 0, 7);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        footer.Controls.Add(Link("Need help?", () => ShowPage(help)));
        footer.Controls.Add(Link("Test controller", TestController)); page.Controls.Add(footer, 0, 8);
        PreparePage(home, page, 440);
        home.Resize += (_, _) => {
            float scale = DeviceDpi / 96f; bool compact = home.ClientSize.Height < 600 * scale;
            foreach (var item in new[] { (0, compact ? 36 : 48), (1, compact ? 24 : 32), (2, compact ? 44 : 50), (3, 54), (6, compact ? 54 : 58), (7, compact ? 28 : 32), (8, compact ? 34 : 40) })
                page.RowStyles[item.Item1].Height = item.Item2 * scale;
            headline.Font = home.ClientSize.Width < 470 * scale ? compactTitleFont : titleFont;
        };
    }
    private static void StylePicker(ChoicePicker picker)
    {
        picker.Dock = DockStyle.Fill; picker.TextAlign = ContentAlignment.MiddleLeft; picker.AutoEllipsis = true;
        picker.FlatStyle = FlatStyle.Flat; picker.FlatAppearance.BorderColor = Color.FromArgb(203, 214, 201);
        picker.BackColor = Color.White; picker.Padding = new Padding(8, 0, 4, 0); picker.MinimumSize = new Size(120, 36);
    }
    private TableLayoutPanel SecondaryPage(SetupScrollPanel surface, string title, int minimumHeight)
    {
        var layout = new TableLayoutPanel { Padding = new Padding(32, 20, 32, 24), ColumnCount = 1, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control control, int height) { layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); layout.Controls.Add(control, 0, layout.RowStyles.Count - 1); }
        var back = Link("‹  Back", () => ShowPage(home)); Row(back, 44); Row(TextLabel(title, 25, true), 56);
        PreparePage(surface, layout, minimumHeight); return layout;
    }
    private static void AddRow(TableLayoutPanel layout, Control control, int height)
    {
        int row = layout.RowStyles.Count; layout.RowCount = row + 1; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); layout.Controls.Add(control, 0, row);
    }
    private void BuildSettings()
    {
        var layout = SecondaryPage(settings, "Settings", 620);
        AddRow(layout, TextLabel("The app chooses these for you when it can."), 42);
        AddRow(layout, TextLabel("Controller Wi-Fi", 10, true), 28); StylePicker(wifi); AddRow(layout, wifi, 42);
        AddRow(layout, TextLabel("Internet connection", 10, true), 30); StylePicker(uplink); AddRow(layout, uplink, 42);
        wifi.SelectedIndexChanged += (_, _) => { candidates.SetItems([]); searchEmpty = false; ApplyState(); };
        uplink.SelectedIndexChanged += (_, _) => { candidates.SetItems([]); searchEmpty = false; ApplyState(); };
        requirements.Dock = DockStyle.Fill; requirements.Margin = new Padding(0, 14, 0, 0); requirements.ForeColor = Palette.Muted; AddRow(layout, requirements, 62);
        detail.Dock = DockStyle.Fill; detail.ForeColor = Palette.Muted; detail.Font = new Font("Segoe UI", 9); AddRow(layout, detail, 64);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var b in new[] { disconnect, refresh, forget }) { b.Dock = DockStyle.Fill; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(203, 214, 201); b.Margin = new Padding(0, 3, 8, 3); b.Height = 38; }
        disconnect.Text = "Disconnect"; disconnect.Click += async (_, _) => await ConnectClick();
        refresh.Text = "Check setup"; refresh.Click += async (_, _) => await RefreshCapabilities(); actions.Controls.Add(disconnect, 0, 0); actions.Controls.Add(refresh, 1, 0); AddRow(layout, actions, 46);
        forget.Text = "Forget controller…"; forget.Click += async (_, _) => await ForgetClick(); AddRow(layout, forget, 44);
        var links = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        links.Controls.Add(Link("Open logs", OpenLogs)); links.Controls.Add(Link("Project & help", () => OpenUrl("https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows"))); AddRow(layout, links, 44);
    }
    private void BuildHelp()
    {
        var layout = SecondaryPage(help, "No flashing light?", 600);
        AddRow(layout, TextLabel("Touch the green logo. It doesn't click.", 12), 44);
        AddRow(layout, new ControllerIllustration { Dock = DockStyle.Fill, Mode = IllustrationMode.Location }, 200);
        AddRow(layout, TextLabel("1   Charge with a wall USB charger.\n2   Touch the green logo for 15 seconds.\n3   Let go. Wait 5 seconds.\n4   Touch it for 3 seconds, until it flashes.", 11), 112);
        var back = new ActionButton { Text = "Got it", Dock = DockStyle.Fill }; back.Click += (_, _) => ShowPage(home); AddRow(layout, back, 56);
        AddRow(layout, Link("More help & troubleshooting", () => OpenUrl("https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/blob/main/docs/compatibility.md")), 40);
    }
    private void ShowPage(SetupScrollPanel selected)
    {
        foreach (var surface in new[] { home, settings, help }) surface.Visible = surface == selected;
        selected.BringToFront(); selected.AutoScrollPosition = Point.Empty;
        AcceptButton = selected == home ? primary : null;
    }
    private void Present(string title, string text, string action, NextAction next, IllustrationMode art, string note = "")
    {
        headline.Text = title; instruction.Text = text; primary.Text = action; primary.AccessibleName = action;
        nextAction = next; illustration.Mode = art; hint.Text = note;
    }
    private void ApplyState()
    {
        bool locked = busy || owner.CommandPending || exiting;
        badge.Text = model.State == ControllerState.Connected ? "●  CONNECTED WIRELESSLY" : model.Paired ? (Running && !model.TransportReady ? "●  CONNECTING" : "○  DISCONNECTED · PAIRING SAVED") : "○  FIRST SETUP";
        badge.ForeColor = model.State == ControllerState.Connected ? Color.FromArgb(49, 111, 29) : Palette.Muted;
        if (tray is not null) tray.Text = "SHIELD · " + StatusModel.Title(model.State);
        bool multiple = candidates.Items.Count > 1 && !model.Paired && !locked;
        candidates.Visible = multiple; page.RowStyles[5].Height = multiple ? 48 * DeviceDpi / 96f : 0;
        candidates.Enabled = multiple && !Running;
        if (locked)
        {
            bool pairing = progress is "Searching" or "Pairing";
            Present(progress == "Searching" ? "Looking for it…" : progress == "Pairing" ? "Pairing…" : progress == "Installing" ? "Finish the installer." : "Just a moment…",
                pairing ? "Keep the green logo flashing.\nWe'll do the rest." : progress == "Installing" ? "Follow the steps in the window that opened.\nThen come back here." : "Getting everything ready.",
                pairing ? "Connecting…" : "Please wait…", NextAction.None, pairing ? IllustrationMode.Searching : IllustrationMode.Error,
                "You can close this window. Setup will keep going.");
        }
        else if (capabilities?.RecoveryRequired == true)
            Present("A quick fix first.", "The last connection didn't finish closing.\nLet's put it right.", "Fix connection", NextAction.Recover, IllustrationMode.Error);
        else if (restartRequired)
            Present("Nearly there.", "The driver is installed.\nRestart your PC to finish setup.", "Close", NextAction.Hide, IllustrationMode.Error, "Your progress is saved.");
        else if (model.State == ControllerState.Error)
            Present(model.Paired ? "Disconnected" : "Setup needs attention", model.Paired ? "Your pairing is saved.\nConnect to use your controller again." : model.Message, model.Paired ? "Connect" : "Check setup", NextAction.Retry, IllustrationMode.Error, model.Paired ? "Pairing saved · controller input is not active" : "Open Help below if you need a hand.");
        else if (capabilities?.Requirements.FirstOrDefault(r => !r.Available) is Requirement missing)
        {
            string title = "One quick setup.";
            (string text, string action, NextAction next) = missing.Id switch {
                "npcap" => ("One tool connects your controller.\nFollow its installer when it opens.", "Install connection tool", NextAction.InstallNpcap),
                "vigem" => ("One driver lets games see your controller.\nFollow its installer when it opens.", "Install gamepad driver", NextAction.InstallVigem),
                "wifi" => ("Turn on your PC's Wi-Fi, then check again.", "Check again", NextAction.Retry),
                "pairing-radio" => ("Keep just one Wi-Fi adapter turned on.\nThen check again.", "Check again", NextAction.Retry),
                "uplink" => ("Connect your PC to the internet.\nAn Ethernet cable works best.", "Check again", NextAction.Retry),
                "windows" => ("This app needs Windows 11, version 24H2\nor newer, on a 64-bit PC.", "Check again", NextAction.Retry),
                "package" => ("Some app files are missing or changed.\nRun the app installer again.", "Check again", NextAction.Retry),
                _ => ("Your PC needs a setup change.\nOpen Settings to see what needs attention.", "Open settings", NextAction.Settings)
            };
            Present(title, text, action, next, IllustrationMode.Error, missing.Id == "npcap" ? "Npcap · a Windows approval may appear" : missing.Id == "vigem" ? "ViGEmBus · a Windows approval may appear" : "");
        }
        else if (capabilities is null)
            Present("Checking your PC…", "This will only take a moment.", "Please wait…", NextAction.None, IllustrationMode.Wake);
        else if (!model.Paired && (wifi.SelectedItem is not AdapterChoice || uplink.SelectedItem is not AdapterChoice))
            Present("Choose a connection.", "Your PC has more than one connection.\nChoose which one to use in Settings.", "Choose connection", NextAction.Settings, IllustrationMode.Error);
        else if (!model.Paired && multiple)
            Present("Choose yours.", "We found more than one controller.\nChoose the one with the flashing logo.", "Pair this controller", NextAction.Pair, IllustrationMode.Pair);
        else if (!model.Paired)
            Present(searchEmpty ? "Not found yet." : "Let's connect.", searchEmpty ? "Keep the green logo flashing.\nThen try again." : "Unplug the USB cable from your PC.\nTouch the green logo until it flashes.",
                searchEmpty ? "Try again" : "It's flashing", NextAction.Pair, IllustrationMode.Pair, "Touch the logo gently. It doesn't click.");
        else if (model.State == ControllerState.Connected)
            Present("Connected", "Open your game.\nYour controller is connected wirelessly.", "Hide to tray", NextAction.Hide, IllustrationMode.Connected, "Closing this window keeps your controller connected.");
        else if (Running)
            Present(model.TransportReady ? "Not connected yet" : "Connecting…", model.TransportReady ? "Tap the green logo once.\nThen move a stick." : "Getting your wireless connection ready.", "Hide to tray", NextAction.Hide, IllustrationMode.Wake, "Pairing saved · waiting for controller input");
        else
            Present("Disconnected", "Your pairing is saved.\nConnect, then tap the green logo once.", "Connect", NextAction.Connect, IllustrationMode.Wake, "Your pairing is saved on this PC.");
        primary.Enabled = !locked && nextAction != NextAction.None && (!multiple || candidates.SelectedItem is Candidate);
        if (nextAction is NextAction.InstallNpcap or NextAction.InstallVigem or NextAction.Pair or NextAction.Recover) primary.Enabled &= !Running;
        trayConnect.Text = disconnect.Text = Running ? "Disconnect" : "Connect";
        trayConnect.Enabled = disconnect.Enabled = !locked && (Running || model.Paired && Ready);
        trayPair.Enabled = !locked && !Running && !model.Paired && Ready;
        trayForget.Enabled = forget.Enabled = !locked && !Running && capabilities?.RecoveryRequired == false;
        refresh.Enabled = !locked && !Running;
        wifi.Enabled = uplink.Enabled = !locked && !Running && !model.Paired;
        requirements.Text = capabilities is null ? "Checking setup…" : RequirementSummary(capabilities);
        detail.Text = capabilities?.Requirements.FirstOrDefault(r => !r.Available)?.Label
            ?? (model.Paired ? "To change adapters, disconnect and forget this controller first." : "Wi-Fi carries controller input. Bluetooth isn't used.");
    }
    private async Task PrimaryClick()
    {
        switch (nextAction)
        {
            case NextAction.Pair: await PairClick(); break;
            case NextAction.Connect: await ConnectClick(); break;
            case NextAction.Hide: if (!preview) Hide(); break;
            case NextAction.Recover: await RecoverClick(); break;
            case NextAction.Retry: await RetryClick(); break;
            case NextAction.InstallNpcap: await InstallDependency("Npcap"); break;
            case NextAction.InstallVigem: await InstallDependency("ViGEmBus"); break;
            case NextAction.Settings: ShowPage(settings); break;
        }
    }
    private async Task Work(Func<Task> operation)
    {
        if (preview || busy || exiting || owner.CommandPending) return;
        var finished = activeWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        busy = true; ApplyState();
        try { await operation(); }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception e)
        {
            candidates.SetItems([]);
            model.Fail(!File.Exists(OwnerClient.Executable) ? "The app needs to be installed again." : e is TimeoutException ? "Setup is still finishing. Please wait a moment." : "That didn't work. Try again, or open Help.");
        }
        finally { busy = false; ApplyState(); finished.TrySetResult(); }
    }
    private void ApplyCapabilities(Capabilities value)
    {
        if (value.Requirements.Length == 0) throw new InvalidDataException("The setup check returned no requirements.");
        capabilities = value; model.ApplyCapabilities(value);
        SetAdapters(wifi, value.Wifi, value.SelectedWifi); SetAdapters(uplink, value.Uplink, value.SelectedUplink);
    }
    private async Task RefreshCapabilities(bool autoConnect = false)
    {
        progress = "Checking";
        await Work(async () => { var value = await owner.GetCapabilities(); if (!exiting) ApplyCapabilities(value); });
        if (autoConnect && !restartRequired && !automaticConnectAttempted && model.State != ControllerState.Error && PairingFlow.CanStart(capabilities))
        { automaticConnectAttempted = true; await ConnectClick(); }
    }
    internal static string RequirementSummary(Capabilities c) => c.Requirements.All(r => r.Available)
        ? "✓  Everything is installed and ready."
        : "Needs setup: " + string.Join(", ", c.Requirements.Where(r => !r.Available).Select(r => r.Id switch { "windows" => "Windows version", "npcap" => "Npcap", "vigem" => "ViGEmBus", "package" => "application files", "wifi" => "Wi-Fi access", "pairing-radio" => "Wi-Fi selection", "uplink" => "internet connection", _ => "connection requirements" }));
    private static void SetAdapters(ChoicePicker control, AdapterChoice[] values, string? selected)
    {
        string? previous = (control.SelectedItem as AdapterChoice)?.Guid;
        control.SetItems(values);
        int i = Array.FindIndex(values, x => string.Equals(x.Guid, selected ?? previous, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) control.SelectedIndex = i; else if (values.Length == 1) control.SelectedIndex = 0;
    }
    private async Task ConnectClick()
    {
        progress = "Connecting";
        await Work(async () => {
            if (owner.Running) { await owner.StopSession(); model.SetDisconnected(); }
            else if (Ready && PairingFlow.CanStart(capabilities)) { model.SetDisconnected(); owner.StartSession(); }
        });
    }
    private async Task PairClick()
    {
        if (Running || !Ready || model.Paired || wifi.SelectedItem is not AdapterChoice w || uplink.SelectedItem is not AdapterChoice u) return;
        Candidate? target = candidates.SelectedItem as Candidate;
        progress = target is null ? "Searching" : "Pairing";
        await Work(async () => {
            model.BeginPairing(); searchEmpty = false; ApplyState();
            void Progress(string value) { progress = value; ApplyState(); }
            PairingResult result = target is null
                ? await PairingFlow.FindAndPair(owner, w.Guid, u.Guid, Progress, () => !exiting)
                : await PairingFlow.PairSelected(owner, target, w.Guid, u.Guid, Progress, () => !exiting);
            if (exiting) return;
            candidates.SetItems(result.Choices); // Multiple results deliberately start with no selection.
            if (result.Capabilities is Capabilities value)
            {
                ApplyCapabilities(value);
                if (PairingFlow.CanStart(value)) { automaticConnectAttempted = true; owner.StartSession(); }
            }
            else { searchEmpty = result.Choices.Length == 0; model.SetDisconnected(); }
        });
    }
    private async Task RetryClick()
    {
        progress = "Checking";
        if (owner.Running) await Work(async () => await owner.StopSession());
        if (!owner.Running && !owner.CommandPending) await RefreshCapabilities(autoConnect: false);
        if (!exiting && Ready && model.State != ControllerState.Error && PairingFlow.CanStart(capabilities)) await ConnectClick();
    }
    private async Task RecoverClick()
    {
        bool recovered = false; progress = "Recovering";
        await Work(async () => { await owner.Recover(); recovered = true; });
        if (recovered && !exiting) await RefreshCapabilities(autoConnect: true);
    }
    private async Task InstallDependency(string component)
    {
        bool installed = false; progress = "Installing";
        await Work(async () => {
            if (owner.Running) throw new InvalidOperationException("Disconnect before installing a driver.");
            string helper = Path.Combine(OwnerClient.Root, "bin", "Install-Dependencies.ps1");
            if (!File.Exists(helper)) throw new FileNotFoundException("The dependency installer is missing.");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Component", component }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("The driver installer could not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 3010) { restartRequired = true; return; }
            if (process.ExitCode != 0) throw new InvalidOperationException("The driver installer did not complete.");
            installed = true;
        });
        if (installed && !exiting) await RefreshCapabilities(autoConnect: true);
    }
    private async Task ForgetClick()
    {
        if (preview || busy || Running || owner.CommandPending || capabilities?.RecoveryRequired != false) return;
        ShowWindow();
        if (MessageBox.Show(this, "Forget this controller?\n\nYou'll need to pair it again to play wirelessly.", "Forget controller", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool forgotten = false; progress = "Checking";
        await Work(async () => { await owner.Forget(); forgotten = true; candidates.SetItems([]); searchEmpty = false; automaticConnectAttempted = false; });
        if (forgotten && !exiting) { await RefreshCapabilities(); ShowPage(home); }
    }
    private void OpenUrl(string url) { if (!preview) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    private void OpenLogs() { if (!preview && Directory.Exists(owner.LogDirectory)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { owner.LogDirectory } }); }
    private void TestController() { if (!preview) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe")) { UseShellExecute = true, ArgumentList = { "joy.cpl" } }); }
    internal void ShowWindow() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    internal async Task ExitAsync()
    {
        if (exiting || preview) return;
        if (busy && !owner.Running && !owner.CommandPending) { ShowWindow(); return; }
        exiting = true; ShowWindow(); Enabled = false; ApplyState();
        try { if (activeWork is not null) await activeWork.Task; await owner.StopSession(); allowClose = true; tray!.Visible = false; Close(); }
        catch { Enabled = true; exiting = false; model.Fail("Still closing the connection. Wait, then try Exit again."); ApplyState(); }
    }
    internal void RenderPreview(PreviewScene scene, string path, Size? viewport = null)
    {
        if (viewport is Size size) Size = size;
        bool paired = scene is PreviewScene.PairedDisconnected or PreviewScene.Connected or PreviewScene.Connecting or PreviewScene.Recovery or PreviewScene.Settings;
        ApplyCapabilities(new(paired, scene == PreviewScene.Recovery,
            [new("windows", "Windows 11 24H2 or newer", true), new("npcap", "Npcap connection tool", scene is not (PreviewScene.MissingDriver or PreviewScene.RestartRequired or PreviewScene.InstallFailed)),
             new("vigem", "ViGEmBus gamepad driver", true), new("package", "Application files", true), new("wifi", "Wi-Fi available", true),
             new("pairing-radio", "One enabled Wi-Fi adapter", true), new("uplink", "Active internet connection", true)],
            [new("preview", "Wi-Fi")], [new("preview", "Ethernet")], "preview", "preview"));
        if (scene is PreviewScene.Pairing or PreviewScene.Provisioning) { model.BeginPairing(); busy = true; progress = scene == PreviewScene.Pairing ? "Searching" : "Pairing"; }
        else if (scene is PreviewScene.Connected or PreviewScene.PairedDisconnected or PreviewScene.Connecting)
        {
            previewRunning = true;
            using var e = JsonDocument.Parse(scene == PreviewScene.Connected ? "{\"connected\":true,\"transport_ready\":true}" : scene == PreviewScene.Connecting ? "{}" : "{\"transport_ready\":true}"); model.ApplySession(e.RootElement);
        }
        else if (scene == PreviewScene.Error) model.Fail("That didn't work. Try again, or open Help.");
        else if (scene == PreviewScene.InstallFailed) model.Fail("The installer didn't finish. Please try again.");
        else if (scene == PreviewScene.RestartRequired) restartRequired = true;
        else if (scene == PreviewScene.NetworkChoice) uplink.SetItems(new AdapterChoice[] { new("one", "Ethernet"), new("two", "Other internet connection") });
        else if (scene == PreviewScene.MultipleControllers) candidates.SetItems(new Candidate[] { new("first", "SHIELD Controller · 1a7e"), new("second", "SHIELD Controller · 9b42") });
        else if (scene == PreviewScene.NoController) searchEmpty = true;
        ApplyState();
        if (scene == PreviewScene.Settings) ShowPage(settings); else if (scene == PreviewScene.Help) ShowPage(help);
        static void PrepareControl(Control c) { _ = c.Handle; c.PerformLayout(); foreach (Control child in c.Controls) PrepareControl(child); }
        PrepareControl(this); PerformLayout();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        var active = scene == PreviewScene.Settings ? settings : scene == PreviewScene.Help ? help : home;
        string? scrolledPath = null;
        if (active.VerticalScroll.Visible)
        {
            active.ScrollToBottom(); scrolledPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-scrolled.png");
            using var bottom = new Bitmap(Width, Height); DrawToBitmap(bottom, new Rectangle(Point.Empty, Size)); bottom.Save(scrolledPath);
        }
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new { scene = scene.ToString(), state = model.State.ToString(), capture = "WinForms DrawToBitmap of production controls", visible = Visible, show_in_taskbar = ShowInTaskbar,
            owner_or_radio_calls = 0, dimensions = new { width = bitmap.Width, height = bitmap.Height }, heading = headline.Text, primary_action = primary.Text, primary_enabled = primary.Enabled,
            candidate_selection_visible = candidates.Visible, candidate_selected = candidates.SelectedItem is not null, settings_visible = scene == PreviewScene.Settings,
            selected_wifi = wifi.SelectedItem?.ToString(), selected_uplink = uplink.SelectedItem?.ToString(), scrolled_capture = scrolledPath is not null,
            png_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }));
    }
    protected override void Dispose(bool disposing) { if (disposing) { stateTimer.Dispose(); tray?.Dispose(); owner.Dispose(); } base.Dispose(disposing); if (disposing) { appIcon.Dispose(); titleFont.Dispose(); compactTitleFont.Dispose(); } }
}
