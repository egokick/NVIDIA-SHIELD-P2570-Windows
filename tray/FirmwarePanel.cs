namespace ShieldController;

internal sealed class FirmwarePanel : Panel
{
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(32, 20, 32, 24), Margin = Padding.Empty };
    private readonly TableLayoutPanel history = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
    private readonly Label installed, heading, evidence, notes;
    private readonly LinkLabel source;
    private readonly LinkLabel versionsBack;
    private readonly Control[] overviewControls, detailControls;
    private readonly List<(FirmwareEntry Entry, Button Button)> entries = [];
    private string? current;
    private bool initialized;
    internal FirmwareEntry Selected { get; private set; } = FirmwareCatalog.Entries[0];
    internal string InstalledText => installed.Text;
    internal bool IsShowingDetails { get; private set; }
    internal bool TextFits => layout.Controls.Cast<Control>().Where(control => IsShowingDetails ? !overviewControls.Contains(control) : !detailControls.Contains(control))
        .Concat(IsShowingDetails ? Enumerable.Empty<Control>() : history.Controls.Cast<Control>()).Where(control => control is Label or Button).All(control =>
        TextRenderer.MeasureText(control.Text, control.Font, new Size(Math.Max(1, control.ClientSize.Width - control.Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= control.ClientSize.Height - control.Padding.Vertical);

    internal FirmwarePanel(Action back, Action<string> openUrl)
    {
        Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BackColor = Palette.Background;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(layout);
        Add(Link("‹  Back", back));
        Add(TextBlock("Controller firmware", 25, true));
        Add(TextBlock("Original NVIDIA SHIELD P2570", 11));
        installed = TextBlock("", 12, true); Add(installed);
        Add(TextBlock("Known versions & documented updates", 12, true));
        Add(TextBlock("Select an entry to read its changes and source. This bundled history is incomplete; some release notes and version numbers are unavailable.", 10));
        history.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Add(history);
        foreach (var entry in FirmwareCatalog.Entries) AddEntry(entry);
        overviewControls = layout.Controls.Cast<Control>().ToArray();
        versionsBack = Link("‹  All versions", ShowOverview); Add(versionsBack);
        heading = TextBlock("", 14, true); Add(heading);
        evidence = TextBlock("", 10); evidence.ForeColor = Palette.Muted; Add(evidence);
        notes = TextBlock("", 11); Add(notes);
        source = Link("View source", () => openUrl(Selected.SourceUrl)); Add(source);
        detailControls = [versionsBack, heading, evidence, notes, source];
        Add(TextBlock("How to update", 12, true));
        Add(TextBlock("NVIDIA's update method uses a compatible SHIELD device. Charge the controller, connect wirelessly, then check SHIELD accessories or the controller update notification.", 10));
        Add(Link("NVIDIA update instructions", () => openUrl("https://support-shield.nvidia.com/controller-user-guide/how-to-update-your-controller.htm")));
        SizeChanged += (_, _) => FitText();
        layout.SizeChanged += (_, _) => FitText();
        UpdateStatus(null, false);
        Select(Selected.Id);
        ShowOverview();
    }

    private void Add(Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private static Label TextBlock(string text, float size, bool bold = false) => new() {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        UseMnemonic = false, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
        ForeColor = Palette.Ink, Margin = new Padding(0, 8, 0, 8)
    };
    private static LinkLabel Link(string text, Action clicked)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Palette.Muted, ActiveLinkColor = Palette.Ink,
            UseMnemonic = false, Margin = new Padding(0, 10, 0, 12), AccessibleName = text };
        link.LinkClicked += (_, _) => clicked(); return link;
    }
    private void AddEntry(FirmwareEntry entry)
    {
        var button = new Button { Text = entry.Title, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Palette.Ink, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 3, 0, 3), AccessibleName = entry.Title };
        button.FlatAppearance.BorderColor = Color.FromArgb(203, 214, 201);
        button.Click += (_, _) => OpenEntry(entry.Id);
        entries.Add((entry, button));
        int row = history.RowCount++;
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize)); history.Controls.Add(button, 0, row);
    }
    internal void UpdateStatus(string? version, bool transportReady)
    {
        version = transportReady && ControllerFirmware.IsValid(version) ? version : null;
        string text = version is not null ? "Installed: v" + version : transportReady
            ? "Installed version unavailable · reconnect to try again."
            : "Connect your controller to read its installed version.";
        if (initialized && current == version && installed.Text == text) return;
        current = version; initialized = true; installed.Text = text;
        var detected = entries.Find(item => item.Entry.Id == "detected");
        bool keepSelection = Selected.Id == "detected";
        if (detected.Button is not null)
        {
            int row = history.GetRow(detected.Button);
            history.Controls.Remove(detected.Button); history.RowStyles.RemoveAt(row); history.RowCount--;
            entries.Remove(detected); detected.Button.Dispose();
        }
        if (version is not null && !FirmwareCatalog.Entries.Any(entry => entry.Version == version))
            AddEntry(FirmwareCatalog.Detected(version));
        foreach (var item in entries)
            item.Button.Text = item.Entry.Title + (item.Entry.Version is not null && item.Entry.Version == version ? " · installed" : "");
        Select(keepSelection && version is not null && entries.Any(item => item.Entry.Id == "detected") ? "detected" : Selected.Id == "detected" ? FirmwareCatalog.Entries[0].Id : Selected.Id);
        FitText();
    }
    internal void Select(string id)
    {
        Selected = entries.Single(item => item.Entry.Id == id).Entry;
        heading.Text = Selected.Title; evidence.Text = Selected.Evidence; notes.Text = Selected.Notes;
        source.Text = "View source: " + Selected.SourceTitle;
        foreach (var item in entries)
        {
            item.Button.BackColor = item.Entry.Id == id ? Palette.Soft : Color.White;
            item.Button.AccessibleDescription = item.Entry.Evidence + (item.Entry.Id == id ? ". Selected." : "");
        }
        FitText();
    }
    internal void OpenEntry(string id)
    {
        Select(id);
        SetDetailView(true);
    }
    internal void ShowOverview() => SetDetailView(false);
    private void SetDetailView(bool show)
    {
        IsShowingDetails = show; layout.SuspendLayout();
        foreach (var control in overviewControls) control.Visible = !show;
        foreach (var control in detailControls) control.Visible = show;
        layout.ResumeLayout(true); FitText();
        if (Parent is ScrollableControl scroll) scroll.AutoScrollPosition = Point.Empty;
    }
    private void FitText()
    {
        int width = Math.Max(80, ClientSize.Width - layout.Padding.Horizontal);
        foreach (Control control in layout.Controls.Cast<Control>().Concat(history.Controls.Cast<Control>()))
        {
            if (control is Label) control.MaximumSize = new Size(width, 0);
            if (control is Button button)
            {
                int textWidth = Math.Max(1, width - button.Padding.Horizontal);
                button.Height = Math.Max((int)(48 * DeviceDpi / 96f), TextRenderer.MeasureText(button.Text, button.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + button.Padding.Vertical + 10);
            }
        }
    }
}
