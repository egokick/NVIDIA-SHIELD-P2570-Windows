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

        string binary = Path.GetFullPath(AppContext.BaseDirectory), separator = Path.DirectorySeparatorChar + "debug" + Path.DirectorySeparatorChar;
        int debug = binary.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (debug < 0) throw new IOException("Controls UI self-tests must run from a build under debug.");
        string directory = Path.Combine(binary[..(debug + separator.Length)], "controls-ui-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
        return checks;
    }
}
