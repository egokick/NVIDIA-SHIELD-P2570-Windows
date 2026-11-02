using System.Text.Json;

namespace ShieldController;

internal static class ControlMappingsTests
{
    internal static int Run()
    {
        int checks = 0, saves = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Controls UI: " + message); }
        var initial = ProfileStore.Defaults();
        var malformed = MainForm.LoadControlProfile(() => throw new InvalidDataException("Invalid mapping"));
        Check(malformed.NeedsReset && malformed.Profile.Bindings["a"].Kind == "mouse_left", "invalid profile must display recoverable defaults without saving");
        var loaded = MainForm.LoadControlProfile(() => initial);
        Check(!loaded.NeedsReset && loaded.Profile == initial, "valid profile must remain unchanged");
        ControlProfile? lastSaved = null;
        using (var panel = new ControlMappingsPanel(initial, profile => { saves++; lastSaved = profile; }))
        {
            Check(saves == 0, "loading controls must not save");
            Check(panel.MappingRows.Select(row => row.Id).Order().SequenceEqual(ProfileStore.ControlIds.Order()), "every button must be configurable");
            var changed = initial.Copy(); changed.Sensitivity = 1.45; changed.Bindings["x"] = new() { Kind = "key", Key = 32 };
            panel.SetProfile(changed);
            Check(saves == 0 && panel.SensitivityControl.Value == 145, "displaying an existing profile must not save");
            panel.SensitivityControl.Value = 185;
            Check(saves == 0, "slider changes must debounce");
            panel.SavePendingChanges();
            Check(saves == 1 && lastSaved!.Sensitivity == 1.85 && lastSaved.Bindings["x"].Key == 32, "slider changes must publish the complete profile");
            panel.MappingRows.Single(row => row.Id == "a").CommitChoice(new("F24", "key", 135));
            Check(saves == 2 && lastSaved!.Bindings["a"].Key == 135, "dropdown changes must save immediately");
            panel.ResetDefaults();
            Check(saves == 3 && lastSaved!.Sensitivity == 1 && lastSaved.Bindings.All(item => item.Value == initial.Bindings[item.Key]), "one reset restores all mappings and calibration multiplier");
            Check(panel.SensitivityControl.Value == 100 && panel.MappingRows.All(row => row.CurrentBinding == initial.Bindings[row.Id]), "reset updates every visible control");
            lastSaved!.Bindings["a"].Kind = "none";
            Check(panel.MappingRows.Single(row => row.Id == "a").CurrentBinding.Kind == "mouse_left", "save callbacks cannot mutate displayed controls");
            panel.SensitivityControl.Value = -20; Check(panel.SensitivityControl.Value == 25, "minimum sensitivity");
            panel.SensitivityControl.Value = 1000; Check(panel.SensitivityControl.Value == 300, "maximum sensitivity");
        }
        using (var panel = new ControlMappingsPanel(initial, profile => { saves++; lastSaved = profile; }))
        {
            int before = saves;
            Check(panel.LeftStick.MovementPicker.SelectedItem?.Kind == "cursor" && panel.RightStick.MovementPicker.SelectedItem?.Kind == "scroll", "left stick defaults to cursor and right stick to scrolling");
            panel.LeftStick.SpeedSlider.Value = 160; panel.RightStick.SpeedSlider.Value = 75;
            Check(saves == before, "independent stick sliders debounce"); panel.SavePendingChanges();
            Check(lastSaved!.Sticks.Left.Sensitivity == 1.6 && lastSaved.Sticks.Right.Sensitivity == .75 && lastSaved.Sensitivity == 1, "stick sensitivity is independent of each other and the trackpad");
            panel.LeftStick.MovementPicker.SelectedIndex = 2;
            Check(lastSaved!.Sticks.Left.Mode == "wasd" && lastSaved.Sticks.Right.Mode == "scroll" && !panel.LeftStick.SpeedSlider.Enabled, "stick movement menu saves the selected mode and disables unused cursor speed");
            panel.LeftStick.ToggleAdvanced(); panel.LeftStick.DeadzoneSlider.Value = 10; panel.SavePendingChanges();
            Check(lastSaved!.Sticks.Left.Deadzone == .10 && panel.LeftStick.DeadzoneSlider.Enabled && !panel.LeftStick.CurvePicker.Enabled && !panel.LeftStick.SmoothingPicker.Enabled, "keyboard movement exposes its dead zone while disabling cursor-only response settings");
            panel.LeftStick.ToggleAdvanced();
            panel.LeftStick.MovementPicker.SelectedIndex = 0;
            int compact = panel.RequiredHeight(600, 96), layoutChanges = 0;
            panel.ContentHeightChanged += (_, _) => layoutChanges++;
            panel.ExpandStickOptions();
            Check(layoutChanges == 2 && panel.RequiredHeight(600, 96) > compact && panel.LeftStick.Expanded && panel.RightStick.Expanded, "feel expansion resizes the production scroller");
            panel.LeftStick.CurvePicker.SelectedIndex = 1; panel.LeftStick.SmoothingPicker.SelectedIndex = 1;
            panel.LeftStick.DeadzoneSlider.Value = 12; panel.SavePendingChanges();
            Check(lastSaved!.Sticks.Left.Curve == "linear" && lastSaved.Sticks.Left.Smoothing == "light" && lastSaved.Sticks.Left.Deadzone == .12 && lastSaved.Sticks.Right.Curve == "precise", "feel controls save only their own joystick");
            panel.DpadModePicker.SelectedIndex = 1;
            Check(ProfileStore.DpadIds.All(id => lastSaved!.Bindings[id].Kind == "cursor") && lastSaved!.Bindings["a"].Kind == "mouse_left", "D-pad cursor preset changes all four directions in one action");
            panel.MappingRows.Single(row => row.Id == "dpad_up").CommitChoice(new("Space", "key", 32));
            Check(panel.DpadModePicker.SelectedItem?.Kind == "custom" && lastSaved!.Bindings["dpad_up"].Key == 32, "individual D-pad mapping preserves the other directions and marks custom");
            panel.DpadModePicker.SelectedIndex = 0;
            Check(ProfileStore.DpadIds.All(id => lastSaved!.Bindings[id] == initial.Bindings[id]), "D-pad arrow preset restores arrow keys");
            panel.ResetDefaults();
            Check(lastSaved!.Sticks.Left == new StickSettings() && lastSaved.Sticks.Right == new StickSettings { Mode = "scroll" } && panel.LeftStick.SpeedSlider.Value == 100 && panel.RightStick.SpeedSlider.Value == 100, "reset restores all motion settings and visible sliders");
            lastSaved.Sticks.Left.Mode = "none";
            Check(panel.LeftStick.CurrentSettings.Mode == "cursor", "save callback cannot mutate displayed stick settings");
            Check(!MappingChoices.ForControl("a").Any(choice => choice.Kind == "cursor") && MappingChoices.ForControl("dpad_left").Any(choice => choice.Kind == "cursor"), "cursor choices are available only for D-pad directions");
            panel.LeftStick.DeadzoneSlider.Value = 100;
            Check(panel.LeftStick.DeadzoneSlider.Value == 35, "dead-zone slider uses its own range");
            typeof(SensitivitySlider).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel.LeftStick.DeadzoneSlider, [new KeyEventArgs(Keys.Left)]);
            Check(panel.LeftStick.DeadzoneSlider.Value == 34, "dead-zone keyboard adjustment uses one percent steps");
            using (var movementMenu = panel.RightStick.MovementPicker.PrepareMenu()) {
                movementMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Gamepad only").PerformClick();
                Check(lastSaved!.Sticks.Right.Mode == "none" && panel.RightStick.CurrentSettings.Mode == "none", "native stick menu changes the production setting");
                Check(!movementMenu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Keyboard keys"), "movement menus must not display an empty keyboard submenu");
            }
            foreach (int width in new[] { 600, 480 }) {
                panel.Size = new Size(width, panel.RequiredHeight(width, 96)); _ = panel.Handle; panel.PerformLayout();
                Check(panel.Controls.Cast<Control>().All(control => control.Bottom <= panel.ClientSize.Height && control.Right <= panel.ClientSize.Width), "all controls fit the calculated expanded height at each width");
                Check(panel.LeftStick.Bounds.IntersectsWith(panel.RightStick.Bounds) == false, "stick cards must not overlap");
            }
        }
        int rowSaves = 0;
        using (var row = new MappingRow("y", "Y", (_, _) => rowSaves++, filePicker: _ => null, keyPicker: _ => null))
        {
            var original = new ControlBinding { Kind = "key", Key = 65 }; row.SetBinding(original);
            Check(!row.CommitChoice(new("Run script or program…", "pick_file")) && row.CurrentBinding == original && rowSaves == 0, "cancelled file selection preserves binding without saving");
            Check(!row.CommitChoice(new("Press a keyboard key…", "capture_key")) && row.CurrentBinding == original && rowSaves == 0, "cancelled key capture preserves binding without saving");
        }
        using (var row = new MappingRow("y", "Y", (_, _) => rowSaves++, filePicker: _ => @"C:\Controller tools\Start game.ps1", keyPicker: _ => 0xA0))
        {
            row.SetBinding(new() { Kind = "none" });
            Check(row.CommitChoice(new("Run script or program…", "pick_file")) && row.CurrentBinding.Path == @"C:\Controller tools\Start game.ps1" && rowSaves == 1, "file selection maps a program without executing it");
            Check(row.CommitChoice(new("Press a keyboard key…", "capture_key")) && row.CurrentBinding.Key == 0xA0 && rowSaves == 2, "captured modifier key is preserved");
        }
        string? validationError = null;
        using (var row = new MappingRow("y", "Y", (_, _) => rowSaves++, filePicker: _ => @"C:\Controller tools\Unsafe%NAME%.cmd", validationError: message => validationError = message))
        {
            var original = new ControlBinding { Kind = "key", Key = 65 }; row.SetBinding(original);
            Check(!row.CommitChoice(new("Run script or program…", "pick_file")) && row.CurrentBinding == original && rowSaves == 2, "invalid file selection must preserve binding without saving");
            Check(validationError is not null && validationError.Contains('%'), "invalid file selection must explain how to repair it");
        }
        foreach (int key in new[] { 0x41, 0x30, 0x70, 0x87, 0x10, 0xA3, 0x5B, 0x60, 0x6F, 0xBA, 0xE2, 0x26 })
            Check(MappingChoices.All.Any(choice => choice.Kind == "key" && choice.Key == key), "keyboard catalog missing " + key);

        // Exercise WinForms' real menu item and Closed event paths, without
        // showing a popup or sending keyboard/mouse input to the desktop.
        static IEnumerable<ToolStripMenuItem> MenuEntries(ToolStripItemCollection items) =>
            items.OfType<ToolStripMenuItem>().SelectMany(item => new[] { item }.Concat(MenuEntries(item.DropDownItems)));
        static void NotifyClosed(ContextMenuStrip menu) => typeof(ToolStripDropDown)
            .GetMethod("OnClosed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, [new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.ItemClicked)]);
        ContextMenuStrip? currentMenu = null;
        int menuSaves = 0, filePicks = 0, keyPicks = 0;
        using (var row = new MappingRow("l1", "L1", (_, _) => menuSaves++,
            filePicker: _ => { filePicks++; Check(currentMenu is { IsDisposed: false, Visible: false }, "file picker must receive a closed, live menu"); return null; },
            keyPicker: _ => { keyPicks++; Check(currentMenu is { IsDisposed: false, Visible: false }, "key picker must receive a closed, live menu"); return 0x41; }))
        {
            row.SetBinding(new() { Kind = "none" });
            _ = row.Handle;
            currentMenu = row.ActionPicker.PrepareMenu(); _ = currentMenu.Handle;
            NotifyClosed(currentMenu);
            Check(!currentMenu.IsDisposed && !currentMenu.Visible, "Closed must not dispose the menu while WinForms is still handling selection");
            MenuEntries(currentMenu.Items).Single(item => item.Text == "Space").PerformClick();
            NotifyClosed(currentMenu);
            Check(menuSaves == 1 && row.CurrentBinding.Key == 0x20 && !currentMenu.IsDisposed, "L1 keyboard selection must survive menu closure");
            var previous = currentMenu;
            currentMenu = row.ActionPicker.PrepareMenu();
            Check(previous.IsDisposed && !currentMenu.IsDisposed, "next opening retires the old menu after click processing");
            MenuEntries(currentMenu.Items).Single(item => item.Text == "Run script or program…").PerformClick();
            NotifyClosed(currentMenu);
            Check(filePicks == 1 && menuSaves == 1 && row.CurrentBinding.Key == 0x20 && !currentMenu.IsDisposed, "cancelled file picker must preserve L1 without premature menu disposal");
            currentMenu = row.ActionPicker.PrepareMenu();
            MenuEntries(currentMenu.Items).Single(item => item.Text == "Press a keyboard key…").PerformClick();
            NotifyClosed(currentMenu);
            Check(keyPicks == 1 && menuSaves == 2 && row.CurrentBinding.Key == 0x41 && !currentMenu.IsDisposed, "key picker must commit L1 after menu closure");
        }
        Check(currentMenu!.IsDisposed, "disposing the picker must release its final owned menu");

        string binary = Path.GetFullPath(AppContext.BaseDirectory), separator = Path.DirectorySeparatorChar + "debug" + Path.DirectorySeparatorChar;
        int debug = binary.LastIndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (debug < 0) throw new IOException("Controls UI self-tests must run from a build under debug.");
        string directory = Path.Combine(binary[..(debug + separator.Length)], "controls-ui-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using (var panel = new ControlMappingsPanel(initial, _ => { }))
        using (var host = new Form { ShowInTaskbar = false })
        {
            host.Controls.Add(panel); _ = host.Handle;
            panel.ExpandStickOptions(); panel.SetDpadMode("cursor");
            panel.Size = new Size(420, panel.RequiredHeight(420, 96)); panel.PerformLayout();
            panel.RightStick.MovementPicker.RenderMenuPreview(Path.Combine(directory, "joystick-movement-menu.png"));
            panel.DpadModePicker.RenderMenuPreview(Path.Combine(directory, "dpad-movement-menu.png"));
            panel.MappingRows.Single(row => row.Id == "dpad_up").RenderChoicesPreview(Path.Combine(directory, "dpad-direction-menu.png"));
            static void Layout(Control control) { _ = control.Handle; control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
            Layout(panel);
            using var image = new Bitmap(panel.Width, panel.Height); panel.DrawToBitmap(image, new Rectangle(Point.Empty, panel.Size));
            image.Save(Path.Combine(directory, "narrow-expanded-controls.png"));
            panel.LeftStick.MovementPicker.SelectedIndex = 2; Layout(panel);
            using var keyboardImage = new Bitmap(panel.LeftStick.Width, panel.LeftStick.Height);
            panel.LeftStick.DrawToBitmap(keyboardImage, new Rectangle(Point.Empty, panel.LeftStick.Size));
            keyboardImage.Save(Path.Combine(directory, "keyboard-stick-card.png"));
            Check(!host.Visible && !host.ShowInTaskbar, "component captures must remain hidden");
        }
        string dependencyReceipt = Path.Combine(directory, "dependency-result-" + Guid.NewGuid().ToString("N") + ".json");
        var receiptWriter = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "@{ok=$true;restart_required=$false} | ConvertTo-Json | Set-Content -LiteralPath '" + dependencyReceipt.Replace("'", "''") + "' -Encoding UTF8" })
            receiptWriter.ArgumentList.Add(argument);
        using (var writer = System.Diagnostics.Process.Start(receiptWriter) ?? throw new IOException("Could not create the PowerShell receipt fixture."))
        {
            var stdout = writer.StandardOutput.ReadToEndAsync(); var stderr = writer.StandardError.ReadToEndAsync();
            if (!writer.WaitForExit(10000)) { writer.Kill(); throw new IOException("The PowerShell receipt fixture timed out."); }
            Check(writer.ExitCode == 0 && string.IsNullOrEmpty(stderr.GetAwaiter().GetResult()), "PowerShell must produce the real dependency receipt encoding");
            stdout.GetAwaiter().GetResult();
        }
        byte[] receiptBytes = File.ReadAllBytes(dependencyReceipt);
        Check(receiptBytes.Length > 3 && receiptBytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "PowerShell 5.1 fixture must include the UTF-8 BOM");
        using (var receipt = MainForm.ReadDependencyReceipt(dependencyReceipt))
            Check(receipt.RootElement.GetProperty("ok").GetBoolean() && !receipt.RootElement.GetProperty("restart_required").GetBoolean(), "tray repair must accept PowerShell's successful receipt");
        using (var form = new MainForm(preview: true))
        {
            string connected = Path.Combine(directory, "connected.png");
            form.RenderPreview(PreviewScene.Connected, connected, new Size(1040, 820));
            using var connectedReceipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(connected, ".json")));
            Check(connectedReceipt.RootElement.GetProperty("controls_panel_visible").GetBoolean(), "connected state must show controls");
            Check(!form.Visible && !form.ShowInTaskbar, "preview must remain hidden");
            string disconnected = Path.Combine(directory, "disconnected.png");
            form.RenderPreview(PreviewScene.PairedDisconnected, disconnected, new Size(520, 650));
            using var disconnectedReceipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(disconnected, ".json")));
            Check(!disconnectedReceipt.RootElement.GetProperty("controls_panel_visible").GetBoolean() && disconnectedReceipt.RootElement.GetProperty("mapping_rows").GetInt32() == 0, "disconnect must hide all controls");
            Check(form.Size == new Size(520, 650), "explicit capture viewport must remain unchanged");
        }
        foreach (var (scene, action) in new[] { (PreviewScene.LocationPermission, "Allow Wi-Fi access"), (PreviewScene.WifiOff, "Open Wi-Fi settings") })
        {
            using var form = new MainForm(preview: true);
            string path = Path.Combine(directory, scene + ".png");
            form.RenderPreview(scene, path, new Size(520, 650));
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            Check(receipt.RootElement.GetProperty("primary_action").GetString() == action && receipt.RootElement.GetProperty("primary_enabled").GetBoolean(), "missing permission and adapter need distinct actionable buttons");
        }
        return checks;
    }
}
