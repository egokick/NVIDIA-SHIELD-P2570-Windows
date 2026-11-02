using System.Drawing.Drawing2D;

namespace ShieldController;

/// <summary>The connected controller's desktop controls. This surface never executes mappings.</summary>
internal sealed class ControlMappingsPanel : Panel
{
    private readonly Action<ControlProfile> changed;
    private readonly Label title = Label("Your controls", 22, true), subtitle = Label("Desktop shortcuts · gamepad unchanged", 10);
    private readonly RoundedCard sensitivityCard = new();
    private readonly Label sensitivityTitle = Label("Trackpad speed", 12, true), sensitivityValue = Label("100%", 12, true);
    private readonly Label calibrationTitle = Label("Automatic calibration", 9, true), calibrationDetail = Label("Uses your current display", 9);
    private readonly SensitivitySlider sensitivity = new();
    private readonly Label slow = Label("Precise", 9), fast = Label("Fast", 9), saveStatus = Label("Changes save automatically", 9);
    private readonly Button reset = new ResetControlsButton { Text = "↺   Reset all controls to default", AccessibleName = "Reset all controls to default" };
    private readonly List<(Label Heading, MappingRow[] Rows)> groups = [];
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 180 };
    private ControlProfile profile;
    private bool updating;
    internal const int LogicalHeight = 770;
    internal static int HeightForWidth(int width, int dpi) => (int)Math.Round((width < 550 * dpi / 96f ? 1070 : LogicalHeight) * dpi / 96f);

    internal ControlMappingsPanel(ControlProfile profile, Action<ControlProfile> onChanged)
    {
        this.profile = profile.Copy(); changed = onChanged;
        BackColor = Palette.Background; DoubleBuffered = true;
        Controls.AddRange([title, subtitle, sensitivityCard, reset, saveStatus]);
        subtitle.ForeColor = Palette.Muted; saveStatus.ForeColor = Palette.Muted;
        sensitivityCard.Controls.AddRange([sensitivityTitle, sensitivityValue, sensitivity, slow, fast, calibrationTitle, calibrationDetail]);
        sensitivityValue.TextAlign = ContentAlignment.MiddleRight; sensitivity.BackColor = Color.White;
        fast.TextAlign = ContentAlignment.MiddleRight; slow.ForeColor = fast.ForeColor = Palette.Muted;
        calibrationDetail.ForeColor = Palette.Muted; calibrationDetail.AutoEllipsis = true;
        sensitivity.AccessibleName = "Trackpad movement sensitivity";
        sensitivity.ValueChanged += (_, _) => {
            if (updating) return;
            this.profile.Sensitivity = sensitivity.Value / 100.0;
            UpdateSensitivityLabel(); SetSaveStatus("Saving…"); saveTimer.Stop(); saveTimer.Start();
        };
        saveTimer.Tick += (_, _) => SavePendingChanges();
        AddGroup("BUTTONS", [("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y")]);
        AddGroup("D-PAD", [("dpad_up", "↑ Up"), ("dpad_right", "→ Right"), ("dpad_left", "← Left"), ("dpad_down", "↓ Down")]);
        AddGroup("SHOULDERS", [("l1", "L1"), ("r1", "R1"), ("l2", "L2"), ("r2", "R2")]);
        AddGroup("MORE CONTROLS", [("volume_down", "−"), ("volume_up", "+"), ("home", "Home"), ("play_pause", "Play / stop"), ("left_stick", "L stick click"), ("right_stick", "R stick click"), ("back", "Back")]);
        reset.Click += (_, _) => ResetDefaults();
        SetProfile(profile);
    }

    private static Label Label(string text, float size, bool bold = false) => new() {
        Text = text, AutoSize = false, Margin = Padding.Empty, UseMnemonic = false, ForeColor = Palette.Ink, BackColor = Color.Transparent,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), TextAlign = ContentAlignment.MiddleLeft
    };

    private void AddGroup(string caption, (string Id, string Label)[] definitions)
    {
        var heading = Label(caption, 8.5f, true); heading.ForeColor = Palette.Muted; Controls.Add(heading);
        var rows = definitions.Select(item => new MappingRow(item.Id, item.Label, BindingChanged, validationError: message => SetSaveStatus(message, error: true))).ToArray();
        foreach (var row in rows) Controls.Add(row);
        groups.Add((heading, rows));
    }

    private void BindingChanged(string id, ControlBinding value)
    {
        if (updating) return;
        saveTimer.Stop(); profile.Bindings[id] = value; Publish();
    }
    private void Publish() { changed(profile.Copy()); }
    internal SensitivitySlider SensitivityControl => sensitivity;
    internal IEnumerable<MappingRow> MappingRows => groups.SelectMany(group => group.Rows);
    internal void ResetDefaults() { saveTimer.Stop(); SetProfile(ProfileStore.Defaults()); Publish(); }
    internal void SavePendingChanges() { saveTimer.Stop(); Publish(); }
    private void UpdateSensitivityLabel()
    {
        sensitivityValue.Text = $"{sensitivity.Value}%";
        sensitivity.AccessibleDescription = $"{sensitivity.Value} percent of automatic display calibration";
    }
    internal void SetProfile(ControlProfile value)
    {
        updating = true;
        try {
            profile = value.Copy(); sensitivity.Value = (int)Math.Round(profile.Sensitivity * 100);
            foreach (var group in groups) foreach (var row in group.Rows) row.SetBinding(profile.Bindings[row.Id]);
            UpdateSensitivityLabel();
        } finally { updating = false; }
    }
    internal void SetCalibration(string title, string detail)
    {
        calibrationTitle.Text = title; calibrationDetail.Text = detail;
        calibrationDetail.AccessibleDescription = detail;
    }
    internal void SetSaveStatus(string text, bool error = false)
    {
        saveStatus.Text = text; saveStatus.ForeColor = error ? Palette.Error : Palette.Muted;
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        float scale = DeviceDpi / 96f; int S(int n) => (int)Math.Round(n * scale);
        int left = S(22), width = Math.Max(S(280), ClientSize.Width - left - S(24));
        title.SetBounds(left, S(19), width, S(33)); subtitle.SetBounds(left, S(53), width, S(23));
        sensitivityCard.SetBounds(left, S(86), width, S(132));
        int inner = S(17), cardWidth = width - inner * 2;
        sensitivityTitle.SetBounds(inner, S(12), cardWidth - S(74), S(25));
        sensitivityValue.SetBounds(width - inner - S(74), S(12), S(74), S(25));
        sensitivity.SetBounds(inner, S(39), cardWidth, S(38));
        slow.SetBounds(inner, S(74), cardWidth / 2, S(18)); fast.SetBounds(inner + cardWidth / 2, S(74), cardWidth / 2, S(18));
        calibrationTitle.SetBounds(inner, S(94), cardWidth, S(17)); calibrationDetail.SetBounds(inner, S(111), cardWidth, S(17));
        int y = S(232), gap = S(16), columns = ClientSize.Width < S(550) ? 1 : 2;
        int column = (width - gap * (columns - 1)) / columns;
        foreach (var group in groups) {
            group.Heading.SetBounds(left, y, width, S(18)); y += S(22);
            for (int i = 0; i < group.Rows.Length; i++) {
                group.Rows[i].SetBounds(left + (i % columns) * (column + gap), y + (i / columns) * S(34), column, S(31));
            }
            y += ((group.Rows.Length + columns - 1) / columns) * S(34) + S(4);
        }
        reset.SetBounds(left, y + S(4), width, S(43));
        saveStatus.SetBounds(left, y + S(51), width, S(23));
    }
    protected override void Dispose(bool disposing) { if (disposing) saveTimer.Dispose(); base.Dispose(disposing); }
}

internal sealed class MappingRow : Panel
{
    internal string Id { get; }
    private readonly Label name;
    private readonly MappingPicker choice;
    private readonly ToolTip tooltip = new();
    private readonly Action<string, ControlBinding> changed;
    private readonly Func<IWin32Window?, string?> pickFile;
    private readonly Func<IWin32Window?, int?> pickKey;
    private readonly Action<string> validationError;
    private ControlBinding binding = new() { Kind = "none" };
    private bool loading;

    internal MappingRow(string id, string label, Action<string, ControlBinding> onChanged,
        Func<IWin32Window?, string?>? filePicker = null, Func<IWin32Window?, int?>? keyPicker = null, Action<string>? validationError = null)
    {
        Id = id; changed = onChanged; BackColor = Palette.Background;
        pickFile = filePicker ?? PickFile; pickKey = keyPicker ?? PickKey;
        this.validationError = validationError ?? (_ => { });
        name = new Label { Text = label, Font = new Font("Segoe UI Semibold", 10), ForeColor = Palette.Ink, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
        choice = new MappingPicker { AccessibleName = label + " desktop action" };
        choice.SelectedIndexChanged += (_, _) => SelectChoice();
        Controls.AddRange([name, choice]);
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent); float scale = DeviceDpi / 96f;
        int labelWidth = (int)Math.Round(92 * scale);
        name.SetBounds(0, 0, labelWidth, Height);
        choice.SetBounds(labelWidth, 0, Math.Max(76, Width - labelWidth), Height);
    }
    internal void SetBinding(ControlBinding value)
    {
        loading = true;
        try {
            binding = new ControlBinding { Kind = value.Kind, Key = value.Key, Path = value.Path };
            var items = MappingChoices.All.ToList();
            int selected = -1;
            for (int i = 0; i < items.Count; i++) if (items[i].Matches(binding)) { selected = i; break; }
            if (selected < 0) {
                var custom = new MappingChoice(binding.Kind == "launch" ? "Run · " + Path.GetFileName(binding.Path) : MappingChoices.KeyName(binding.Key ?? 0), binding.Kind, binding.Key, binding.Path);
                items.Insert(0, custom); selected = 0;
            }
            choice.SetItems(items); choice.SelectedIndex = selected;
            tooltip.SetToolTip(choice, binding.Kind == "launch" ? binding.Path : choice.Text);
        } finally { loading = false; }
    }
    private void SelectChoice()
    {
        if (loading || choice.SelectedItem is not MappingChoice selected) return;
        CommitChoice(selected);
    }
    internal ControlBinding CurrentBinding => binding with { };
    internal void RenderChoicesPreview(string path) => choice.RenderMenuPreview(path);
    internal bool CommitChoice(MappingChoice selected)
    {
        ControlBinding next;
        if (selected.Kind == "pick_file") {
            string? selectedPath = pickFile(FindForm());
            if (selectedPath is null) { SetBinding(binding); return false; }
            next = new ControlBinding { Kind = "launch", Path = Path.GetFullPath(selectedPath) };
        } else if (selected.Kind == "capture_key") {
            if (pickKey(FindForm()) is not int key) { SetBinding(binding); return false; }
            next = new ControlBinding { Kind = "key", Key = key };
        } else next = new ControlBinding { Kind = selected.Kind, Key = selected.Key, Path = selected.Path };
        var proposed = ProfileStore.Defaults(); proposed.Bindings[Id] = next;
        try { ProfileStore.Validate(proposed); }
        catch (InvalidDataException error) { SetBinding(binding); validationError(error.Message.StartsWith("Batch-file") ? "Rename this batch file to remove % and !." : error.Message); return false; }
        SetBinding(next); changed(Id, next); return true;
    }
    private static string? PickFile(IWin32Window? parent)
    {
        using var picker = new OpenFileDialog { Title = "Choose a script or program", CheckFileExists = true, Multiselect = false,
            Filter = "Programs and scripts (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1", RestoreDirectory = true };
        return picker.ShowDialog(parent) == DialogResult.OK ? picker.FileName : null;
    }
    private static int? PickKey(IWin32Window? parent)
    {
        using var capture = new KeyCaptureForm();
        return capture.ShowDialog(parent) == DialogResult.OK ? capture.SelectedKey : null;
    }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}

// A native keyboard-accessible button and menu, painted identically in the app and hidden captures.
internal sealed class MappingPicker : Button
{
    private MappingChoice[] items = [];
    private int selectedIndex = -1;
    private bool hover;
    internal event EventHandler? SelectedIndexChanged;
    internal MappingChoice? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : null;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex {
        get => selectedIndex;
        set {
            if (value < -1 || value >= items.Length) throw new ArgumentOutOfRangeException(nameof(value));
            bool changed = value != selectedIndex; selectedIndex = value;
            Text = SelectedItem?.Label ?? "Choose action"; Invalidate();
            if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal MappingPicker()
    {
        Font = new Font("Segoe UI", 9.5f); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        ForeColor = Palette.Ink; BackColor = Palette.Background; Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    internal void SetItems(IEnumerable<MappingChoice> values) { items = values.ToArray(); SelectedIndex = -1; }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var menu = BuildMenu();
        menu.Closed += (_, _) => menu.Dispose(); menu.Show(this, new Point(0, Height));
    }
    internal void RenderMenuPreview(string path)
    {
        using var menu = BuildMenu();
        _ = menu.Handle; menu.PerformLayout(); menu.Size = menu.GetPreferredSize(Size.Empty); menu.PerformLayout();
        using var bitmap = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size)); bitmap.Save(path);
        if (menu.Visible) throw new InvalidOperationException("Menu preview must remain hidden.");
    }
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 10), ShowImageMargin = false };
        ToolStripMenuItem Entry(int index) {
            var entry = new ToolStripMenuItem(items[index].Label) { Checked = index == selectedIndex };
            entry.Click += (_, _) => SelectedIndex = index; return entry;
        }
        for (int i = 0; i < items.Length; i++) if (items[i].Kind != "key") menu.Items.Add(Entry(i));
        menu.Items.Add(new ToolStripSeparator());
        var keyboard = new ToolStripMenuItem("Keyboard keys"); menu.Items.Add(keyboard);
        string Group(int key) => key switch {
            >= 0x41 and <= 0x5A => "Letters A–Z", >= 0x30 and <= 0x39 => "Numbers 0–9",
            >= 0x70 and <= 0x87 => "Function keys F1–F24", >= 0x60 and <= 0x6F => "Number pad",
            >= 0xBA and <= 0xE2 => "Punctuation", >= 0xA6 and <= 0xB7 => "Media and browser",
            0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x5D or 0x90 or 0x91 or >= 0xA0 and <= 0xA5 => "Modifiers and locks",
            _ => "Arrows and navigation"
        };
        foreach (var group in items.Select((item, index) => (item, index)).Where(pair => pair.item.Kind == "key").GroupBy(pair => Group(pair.item.Key!.Value))) {
            var category = new ToolStripMenuItem(group.Key);
            foreach (var pair in group) category.DropDownItems.Add(Entry(pair.index));
            keyboard.DropDownItems.Add(category);
        }
        return menu;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Down or (Keys.Alt | Keys.Down) or Keys.F4) { PerformClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f; bool contrast = SystemInformation.HighContrast;
        using var path = Palette.Round(new RectangleF(1, 1, Width - 3, Height - 3), 7 * s);
        using var fill = new SolidBrush(contrast ? SystemColors.Window : hover ? Palette.Soft : Color.White);
        using var outline = new Pen(contrast ? SystemColors.WindowText : Color.FromArgb(214, 223, 209));
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(outline, path);
        var text = new Rectangle((int)(9 * s), 0, Width - (int)(32 * s), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, text, contrast ? SystemColors.WindowText : Palette.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        float x = Width - 14 * s, y = Height / 2f;
        using var pen = new Pen(contrast ? SystemColors.WindowText : Palette.Muted, 1.5f * s);
        e.Graphics.DrawLines(pen, [new PointF(x - 3 * s, y - 1.5f * s), new PointF(x, y + 1.5f * s), new PointF(x + 3 * s, y - 1.5f * s)]);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed record MappingChoice(string Label, string Kind, int? Key = null, string? Path = null)
{
    public override string ToString() => Label;
    internal bool Matches(ControlBinding binding) => Kind == binding.Kind && Key == binding.Key && Path == binding.Path;
}

internal static class MappingChoices
{
    internal static readonly MappingChoice[] All = Build();
    private static MappingChoice[] Build()
    {
        var items = new List<MappingChoice> {
            new("Nothing", "none"), new("Left click", "mouse_left"), new("Right click", "mouse_right"), new("Middle click", "mouse_middle"),
            new("Volume down", "volume_down"), new("Volume up", "volume_up"), new("Play / pause", "media_play_pause"), new("Stop playback", "media_stop"),
            new("Press a keyboard key…", "capture_key"), new("Run script or program…", "pick_file")
        };
        void Key(int key, string name) => items.Add(new MappingChoice(name, "key", key));
        foreach (var key in new[] { 0x26, 0x28, 0x25, 0x27, 0x0D, 0x20, 0x1B, 0x09, 0x08, 0x24, 0x23, 0x21, 0x22, 0x2D, 0x2E }) Key(key, KeyName(key));
        for (int key = 0x41; key <= 0x5A; key++) Key(key, ((char)key).ToString());
        for (int key = 0x30; key <= 0x39; key++) Key(key, ((char)key).ToString());
        for (int key = 0x70; key <= 0x87; key++) Key(key, "F" + (key - 0x6F));
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x5D, 0x14, 0x90, 0x91, 0x2C, 0x13 }) Key(key, KeyName(key));
        for (int key = 0x60; key <= 0x69; key++) Key(key, "Numpad " + (key - 0x60));
        foreach (int key in new[] { 0x6A, 0x6B, 0x6D, 0x6E, 0x6F, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xE2, 0xAD, 0xB0, 0xB1, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC }) Key(key, KeyName(key));
        return items.ToArray();
    }
    internal static string KeyName(int key) => key switch {
        0x26 => "↑ Arrow up", 0x28 => "↓ Arrow down", 0x25 => "← Arrow left", 0x27 => "→ Arrow right", 0x0D => "Enter", 0x20 => "Space", 0x1B => "Escape", 0x09 => "Tab", 0x08 => "Backspace",
        0x24 => "Home", 0x23 => "End", 0x21 => "Page up", 0x22 => "Page down", 0x2D => "Insert", 0x2E => "Delete", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl", 0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x5B => "Left Windows", 0x5C => "Right Windows", 0x5D => "Menu",
        0x14 => "Caps Lock", 0x90 => "Num Lock", 0x91 => "Scroll Lock", 0x2C => "Print Screen", 0x13 => "Pause", 0x6A => "Numpad ×", 0x6B => "Numpad +", 0x6D => "Numpad −", 0x6E => "Numpad decimal", 0x6F => "Numpad ÷",
        0xBA => "; / :", 0xBB => "= / +", 0xBC => ", / <", 0xBD => "− / _", 0xBE => ". / >", 0xBF => "/ / ?", 0xC0 => "` / ~", 0xDB => "[ / {", 0xDC => "\\ / |", 0xDD => "] / }", 0xDE => "' / \"", 0xE2 => "Extra \\ / |",
        0xAD => "Mute", 0xB0 => "Next track", 0xB1 => "Previous track", 0xA6 => "Browser back", 0xA7 => "Browser forward", 0xA8 => "Browser refresh", 0xA9 => "Browser stop", 0xAA => "Browser search", 0xAB => "Browser favourites", 0xAC => "Browser home",
        >= 0x41 and <= 0x5A => ((char)key).ToString(), >= 0x30 and <= 0x39 => ((char)key).ToString(), >= 0x70 and <= 0x87 => "F" + (key - 0x6F), >= 0x60 and <= 0x69 => "Numpad " + (key - 0x60),
        _ => ((Keys)key).ToString()
    };
}

internal sealed class KeyCaptureForm : Form
{
    internal int? SelectedKey { get; private set; }
    internal KeyCaptureForm()
    {
        Text = "Choose a keyboard key"; ClientSize = new Size(380, 150); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent; BackColor = Palette.Background; KeyPreview = true;
        Controls.Add(new Label { Text = "Press the key you want", Font = new Font("Segoe UI Semibold", 19), ForeColor = Palette.Ink, Bounds = new Rectangle(24, 30, 332, 40) });
        Controls.Add(new Label { Text = "One key. Close this window to cancel.", Font = new Font("Segoe UI", 10), ForeColor = Palette.Muted, Bounds = new Rectangle(24, 84, 332, 30) });
    }
    private bool CaptureKey(Keys key)
    {
        int value = (int)(key & Keys.KeyCode);
        if (value is < 8 or > 254) return false;
        SelectedKey = value; DialogResult = DialogResult.OK; Close(); return true;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => CaptureKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
    protected override void OnKeyDown(KeyEventArgs e) { if (CaptureKey(e.KeyCode)) e.Handled = true; base.OnKeyDown(e); }
}

internal sealed class SensitivitySlider : Control
{
    private int value = 100;
    private bool dragging;
    internal event EventHandler? ValueChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int Value { get => value; set { int next = Math.Clamp(value, 25, 300); if (this.value == next) return; this.value = next; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    internal SensitivitySlider() { DoubleBuffered = true; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true); }
    private void FromPointer(int x) { int inset = (int)Math.Round(13 * DeviceDpi / 96f); Value = (int)Math.Round((25 + 275.0 * Math.Clamp((x - inset) / (double)Math.Max(1, Width - inset * 2), 0, 1)) / 5) * 5; }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); Capture = dragging = true; FromPointer(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) FromPointer(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; } }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); switch (e.KeyCode) { case Keys.Left: case Keys.Down: Value -= 5; break; case Keys.Right: case Keys.Up: Value += 5; break; case Keys.Home: Value = 25; break; case Keys.End: Value = 300; break; default: return; } e.Handled = true; }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; float s = DeviceDpi / 96f;
        float inset = 13 * s, width = Math.Max(1, Width - inset * 2), x = inset + width * (Value - 25) / 275f, y = Height / 2f;
        using var track = new Pen(SystemInformation.HighContrast ? SystemColors.GrayText : Palette.Soft, 8 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(SystemInformation.HighContrast ? SystemColors.Highlight : Palette.Green, 8 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, inset, y, inset + width, y); e.Graphics.DrawLine(active, inset, y, x, y);
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.White); using var rim = new Pen(Palette.Ink, 2 * s);
        e.Graphics.FillEllipse(fill, x - 10 * s, y - 10 * s, 20 * s, 20 * s); e.Graphics.DrawEllipse(rim, x - 10 * s, y - 10 * s, 20 * s, 20 * s);
        if (Focused && ShowFocusCues) { using var focus = new Pen(Palette.Ink, s) { DashStyle = DashStyle.Dot }; e.Graphics.DrawEllipse(focus, x - 13 * s, y - 13 * s, 26 * s, 26 * s); }
    }
}

internal sealed class RoundedCard : Panel
{
    internal RoundedCard() { DoubleBuffered = true; BackColor = Palette.Background; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Round(new RectangleF(1, 1, Width - 3, Height - 3), 15 * DeviceDpi / 96f);
        using var brush = new SolidBrush(Color.White); using var line = new Pen(Palette.Soft); e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(line, path);
    }
}

internal sealed class ResetControlsButton : Button
{
    internal ResetControlsButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Font = new Font("Segoe UI Semibold", 10.5f); Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Round(new RectangleF(1, 1, Width - 3, Height - 3), 12 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Palette.Soft); e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Palette.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6));
    }
}
