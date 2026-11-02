using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShieldController;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ControlBinding
{
    [JsonPropertyName("kind"), JsonRequired] public string Kind { get; set; } = "none";
    [JsonPropertyName("key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Key { get; set; }
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StickSettings
{
    [JsonPropertyName("mode"), JsonRequired] public string Mode { get; set; } = "cursor";
    [JsonPropertyName("sensitivity"), JsonRequired] public double Sensitivity { get; set; } = 1;
    [JsonPropertyName("curve"), JsonRequired] public string Curve { get; set; } = "precise";
    [JsonPropertyName("deadzone"), JsonRequired] public double Deadzone { get; set; } = 0.20;
    [JsonPropertyName("smoothing"), JsonRequired] public string Smoothing { get; set; } = "off";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class StickSettingsPair
{
    [JsonPropertyName("left"), JsonRequired] public StickSettings Left { get; set; } = new();
    [JsonPropertyName("right"), JsonRequired] public StickSettings Right { get; set; } = new() { Mode = "scroll" };
    internal StickSettingsPair Copy() => new() { Left = Left with { }, Right = Right with { } };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ControlProfile
{
    [JsonPropertyName("schema"), JsonRequired] public int Schema { get; set; } = 1;
    [JsonPropertyName("sensitivity"), JsonRequired] public double Sensitivity { get; set; } = 1;
    [JsonPropertyName("bindings"), JsonRequired] public Dictionary<string, ControlBinding> Bindings { get; set; } = new(StringComparer.Ordinal);
    // Additive settings default when absent so an update keeps existing button mappings.
    [JsonPropertyName("sticks")] public StickSettingsPair Sticks { get; set; } = new();
    public ControlProfile Copy() => new() { Schema = Schema, Sensitivity = Sensitivity, Bindings = Bindings.ToDictionary(p => p.Key, p => p.Value with { }, StringComparer.Ordinal), Sticks = Sticks.Copy() };
}

internal static class ProfileStore
{
    internal static readonly string[] ControlIds = ["a", "b", "x", "y", "l1", "l2", "r1", "r2", "home", "play_pause", "back", "left_stick", "right_stick", "dpad_up", "dpad_down", "dpad_left", "dpad_right", "volume_down", "volume_up"];
    internal static readonly string[] DpadIds = ["dpad_up", "dpad_right", "dpad_down", "dpad_left"];
    private static readonly HashSet<string> SimpleKinds = ["none", "mouse_left", "mouse_right", "mouse_middle", "volume_down", "volume_up", "media_play_pause", "media_stop"];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 8 };
    internal static string FilePath => Path.Combine(OwnerClient.Root, "controls.json");

    internal static ControlProfile Defaults()
    {
        var profile = new ControlProfile();
        foreach (string id in ControlIds) profile.Bindings[id] = new();
        profile.Bindings["a"] = new() { Kind = "mouse_left" };
        profile.Bindings["b"] = new() { Kind = "mouse_right" };
        profile.Bindings["volume_down"] = new() { Kind = "volume_down" };
        profile.Bindings["volume_up"] = new() { Kind = "volume_up" };
        profile.Bindings["play_pause"] = new() { Kind = "media_play_pause" };
        foreach (var (id, key) in new[] { ("home", 0x24), ("back", 0x1b), ("dpad_up", 0x26), ("dpad_down", 0x28), ("dpad_left", 0x25), ("dpad_right", 0x27) })
            profile.Bindings[id] = new() { Kind = "key", Key = key };
        return profile;
    }

    internal static void Validate(ControlProfile profile)
    {
        if (profile.Schema != 1 || !double.IsFinite(profile.Sensitivity) || profile.Sensitivity is < 0.25 or > 3.0)
            throw new InvalidDataException("The controls profile has invalid sensitivity or format.");
        if (profile.Bindings is null || profile.Bindings.Count != ControlIds.Length || ControlIds.Any(id => !profile.Bindings.ContainsKey(id)))
            throw new InvalidDataException("The controls profile must contain every controller button.");
        if (profile.Sticks is null || profile.Sticks.Left is null || profile.Sticks.Right is null)
            throw new InvalidDataException("Both joystick movement settings are required.");
        foreach (var stick in new[] { profile.Sticks.Left, profile.Sticks.Right })
            if (stick.Mode is not ("cursor" or "scroll" or "arrows" or "wasd" or "none") || stick.Curve is not ("precise" or "linear" or "quick") ||
                stick.Smoothing is not ("off" or "light" or "steady") || !double.IsFinite(stick.Sensitivity) || stick.Sensitivity is < 0.25 or > 3 ||
                !double.IsFinite(stick.Deadzone) || stick.Deadzone is < 0 or > 0.35)
                throw new InvalidDataException("The joystick movement settings are invalid.");
        foreach (var (id, binding) in profile.Bindings)
        {
            if (binding is null) throw new InvalidDataException("A controller binding is missing.");
            if (SimpleKinds.Contains(binding.Kind) || binding.Kind == "cursor" && DpadIds.Contains(id))
            {
                if (binding.Key is not null || binding.Path is not null) throw new InvalidDataException("This action cannot have a key or program.");
            }
            else if (binding.Kind == "key")
            {
                if (binding.Key is null or < 8 or > 254 || binding.Path is not null)
                    throw new InvalidDataException("Choose a valid keyboard key.");
            }
            else if (binding.Kind == "launch")
            {
                string? path = binding.Path;
                if (binding.Key is not null || path is null || System.Text.Encoding.UTF8.GetByteCount(path) > 32700 || path.Any(c => char.IsControl(c) || c == '"') || !Path.IsPathFullyQualified(path) ||
                    !new[] { ".exe", ".cmd", ".bat", ".ps1" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose an EXE, CMD, BAT or PowerShell file using its full path.");
                if (new[] { ".cmd", ".bat" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && (path.Contains('%') || path.Contains('!')))
                    throw new InvalidDataException("Batch-file paths cannot contain % or ! because Windows expands these characters.");
            }
            else throw new InvalidDataException("Unknown controller action.");
        }
    }

    internal static byte[] Serialize(ControlProfile profile)
    {
        Validate(profile);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Options);
        if (bytes.Length > 65536) throw new InvalidDataException("The controls profile is too large.");
        return bytes;
    }
    internal static ControlProfile Deserialize(byte[] bytes)
    {
        if (bytes.Length is < 2 or > 65536) throw new InvalidDataException("The controls profile is too large or empty.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The controls profile must be an object.");
        RejectDuplicates(document.RootElement);
        if (document.RootElement.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
            foreach (var binding in bindings.EnumerateObject())
            {
                if (binding.Value.ValueKind != JsonValueKind.Object || !binding.Value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("A controller action is invalid.");
                string? argument = kind.GetString() switch { "key" => "key", "launch" => "path", _ => null };
                if (binding.Value.EnumerateObject().Any(p => p.Name != "kind" && p.Name != argument))
                    throw new InvalidDataException("A controller action has unexpected fields.");
            }
        var profile = JsonSerializer.Deserialize<ControlProfile>(bytes, Options) ?? throw new InvalidDataException("The controls profile is empty.");
        Validate(profile); return profile;
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new InvalidDataException("The controls profile contains duplicate fields."); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
    internal static ControlProfile Load() => LoadFrom(FilePath);
    internal static ControlProfile LoadFrom(string path)
    {
        if (!File.Exists(path)) return Defaults();
        RequireRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 65536) throw new InvalidDataException("The controls profile is too large.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return Deserialize(bytes);
    }
    internal static void Save(ControlProfile profile)
    {
        Validate(profile); OwnerClient.CreatePrivateDirectory(OwnerClient.Root); SaveTo(FilePath, profile);
    }
    internal static void SaveTo(string path, ControlProfile profile)
    {
        byte[] bytes = Serialize(profile);
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Controls cannot be saved through a directory link.");
        if (File.Exists(path)) RequireRegularFile(path);
        string temporary = Path.Combine(parent, ".controls-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void RequireRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Controls must be stored in a regular file.");
    }
}
