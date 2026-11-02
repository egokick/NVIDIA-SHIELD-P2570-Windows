using System.Text.Json;

internal static class ConnectionSettings
{
    internal static readonly string[] Choices = ["Automatic (prefer 5 GHz)", "5 GHz only", "2.4 GHz only"];
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA-SHIELD-P2570-Windows", "connection.json");
    internal static int Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var fields = doc.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Count(f => f.Name == "schema") != 1 || fields.Count(f => f.Name == "band") != 1 || doc.RootElement.GetProperty("schema").GetInt32() != 1)
            throw new FormatException("Invalid connection settings. Choose a Wi-Fi band in Settings again.");
        return doc.RootElement.GetProperty("band").GetString() switch { "auto" => 0, "5" => 1, "2.4" => 2, _ => throw new FormatException("Unknown Wi-Fi band.") };
    }
    internal static string Serialize(int choice) => JsonSerializer.Serialize(new { schema = 1, band = choice switch { 0 => "auto", 1 => "5", 2 => "2.4", _ => throw new ArgumentOutOfRangeException(nameof(choice)) } });
    internal static int Load()
    {
        CheckPath();
        if (!File.Exists(FilePath)) return 0;
        if (new FileInfo(FilePath).Length > 1024) throw new FormatException("Connection settings are too large.");
        return Parse(File.ReadAllText(FilePath));
    }
    private static void CheckPath()
    {
        for (string? path = FilePath; path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Connection settings cannot use a linked path.");
    }
    internal static void Save(int choice)
    {
        string text = Serialize(choice); CheckPath();
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // Zero means automatic. Explicit choices never switch bands silently.
    internal static int Select(int choice, bool five, bool two) => choice switch {
        0 when five => 5, 0 when two => 2, 1 when five => 5, 2 when two => 2,
        >= 0 and <= 2 => throw new InvalidOperationException("The selected Wi-Fi band is unavailable. Choose Automatic or another band in Settings."),
        _ => throw new ArgumentOutOfRangeException(nameof(choice))
    };
    internal static bool ValidChannel(int channel) => channel is >= 1 and <= 11 or 36 or 40 or 44 or 48;
    internal static bool RetryOnTwo(int preference, int band, bool supportsTwo, int status, bool empty) => preference == 0 && band == 5 && supportsTwo && status is 10 or 11 && empty;
    internal static bool Matches(int channel, int band) => band == 2 ? channel is >= 1 and <= 11 : band == 5 && channel is 36 or 40 or 44 or 48;
    internal static int SelfTest()
    {
        int count = 0;
        void Check(bool value) { count++; if (!value) throw new InvalidOperationException("Band selection test failed."); }
        for (int i = 0; i < 3; i++) Check(Parse(Serialize(i)) == i);
        foreach (string invalid in new[] { "{}", "{\"schema\":1,\"band\":\"auto\",\"band\":\"5\"}", "{\"schema\":2,\"band\":\"auto\"}", "{\"schema\":1,\"band\":\"6\"}" }) {
            bool rejected = false; try { Parse(invalid); } catch (Exception error) when (error is FormatException or KeyNotFoundException) { rejected = true; } Check(rejected);
        }
        Check(Select(0, true, true) == 5); Check(Select(0, false, true) == 2);
        Check(Select(1, true, true) == 5); Check(Select(2, true, true) == 2);
        foreach (var scenario in new[] { (0, false, false), (1, false, true), (2, true, false) }) {
            bool rejected = false; try { Select(scenario.Item1, scenario.Item2, scenario.Item3); } catch (InvalidOperationException) { rejected = true; } Check(rejected);
        }
        foreach (int c in new[] { 1, 6, 11, 36, 40, 44, 48 }) Check(ValidChannel(c));
        foreach (int c in new[] { 0, 12, 14, 35, 52, 149 }) Check(!ValidChannel(c));
        Check(Matches(6, 2) && !Matches(36, 2) && Matches(40, 5) && !Matches(6, 5));
        Check(RetryOnTwo(0, 5, true, 10, true) && RetryOnTwo(0, 5, true, 11, true));
        Check(!RetryOnTwo(1, 5, true, 10, true) && !RetryOnTwo(0, 5, true, 9, true));
        Check(!RetryOnTwo(0, 5, false, 10, true) && !RetryOnTwo(0, 5, true, 10, false));
        return count;
    }
}
