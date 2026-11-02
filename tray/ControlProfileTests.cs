using System.Reflection;
using System.Text;

namespace ShieldController;

internal static class ControlProfileTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("Controls profile check failed."); }
        void Invalid(Action action) { bool invalid = false; try { action(); } catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { invalid = true; } Check(invalid); }
        var defaults = ProfileStore.Defaults();
        Check(defaults.Sensitivity == 1 && defaults.Bindings.Count == 19);
        Check(defaults.Bindings["a"].Kind == "mouse_left" && defaults.Bindings["b"].Kind == "mouse_right");
        Check(defaults.Bindings["left_stick"].Kind == "none" && defaults.Bindings["right_stick"].Kind == "none");
        using var fixture = Assembly.GetExecutingAssembly().GetManifestResourceStream("default-controls.json") ?? throw new IOException("Controls fixture missing.");
        using var fixtureBytes = new MemoryStream(); fixture.CopyTo(fixtureBytes);
        var contract = ProfileStore.Deserialize(fixtureBytes.ToArray());
        Check(contract.Bindings.All(p => defaults.Bindings[p.Key] == p.Value) && contract.Sensitivity == defaults.Sensitivity);
        var changed = defaults.Copy(); changed.Sensitivity = 2; changed.Bindings["a"] = new() { Kind = "key", Key = 65 };
        Check(defaults.Sensitivity == 1 && defaults.Bindings["a"].Kind == "mouse_left");
        var roundtrip = ProfileStore.Deserialize(ProfileStore.Serialize(changed));
        Check(roundtrip.Sensitivity == 2 && roundtrip.Bindings["a"].Key == 65);
        changed.Bindings["x"] = new() { Kind = "launch", Path = @"C:\Program Files\Example & Test\run.ps1" };
        Check(ProfileStore.Deserialize(ProfileStore.Serialize(changed)).Bindings["x"].Path == changed.Bindings["x"].Path);
        foreach (double sensitivity in new[] { 0.0, 4.0, double.NaN }) { var bad = defaults.Copy(); bad.Sensitivity = sensitivity; Invalid(() => ProfileStore.Validate(bad)); }
        var missing = defaults.Copy(); missing.Bindings.Remove("x"); Invalid(() => ProfileStore.Validate(missing));
        var extra = defaults.Copy(); extra.Bindings["unknown"] = new(); Invalid(() => ProfileStore.Validate(extra));
        foreach (var binding in new[] { new ControlBinding { Kind = "key", Key = 0 }, new() { Kind = "key", Key = 255 }, new() { Kind = "launch", Path = "relative.exe" }, new() { Kind = "launch", Path = @"C:\File.txt" }, new() { Kind = "none", Key = 65 }, new() { Kind = "unrecognised" } })
        { var bad = defaults.Copy(); bad.Bindings["x"] = binding; Invalid(() => ProfileStore.Validate(bad)); }
        string json = Encoding.UTF8.GetString(ProfileStore.Serialize(defaults));
        foreach (string root in new[] { "[]", "null", "42", "\"text\"" }) Invalid(() => ProfileStore.Deserialize(Encoding.UTF8.GetBytes(root)));
        Invalid(() => ProfileStore.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"schema\": 1", "\"schema\": 1, \"schema\": 1"))));
        Invalid(() => ProfileStore.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"sensitivity\": 1", "\"sensitivity\": 1, \"extra\": true"))));
        Invalid(() => ProfileStore.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"kind\": \"none\"", "\"kind\": \"none\", \"key\": null"))));
        var calibration = DisplayCalibration.Parse("{\"width\":2560,\"height\":1440,\"dpi\":96,\"diagonal_inches\":31.48,\"physical_size_source\":\"driver-reported\",\"default_gain\":5.37}");
        Check(calibration.Width == 2560 && calibration.Title.Contains("1440") && calibration.Detail.Contains("size"));

        string binary = Path.GetFullPath(AppContext.BaseDirectory), separator = Path.DirectorySeparatorChar + "debug" + Path.DirectorySeparatorChar;
        int debug = binary.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (debug < 0) throw new IOException("Profile self-tests must run from a build under debug.");
        string directory = Path.Combine(binary[..(debug + separator.Length)], "controls-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "controls.json");
        Check(ProfileStore.LoadFrom(path).Bindings["a"].Kind == "mouse_left");
        ProfileStore.SaveTo(path, changed); Check(ProfileStore.LoadFrom(path).Sensitivity == 2);
        ProfileStore.SaveTo(path, defaults); Check(ProfileStore.LoadFrom(path).Bindings["a"].Kind == "mouse_left");
        Check(Directory.GetFiles(directory).Length == 1);
        return checks;
    }
}
