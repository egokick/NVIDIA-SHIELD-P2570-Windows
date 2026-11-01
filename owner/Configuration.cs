using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class Configuration
{
    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("wifi_guid")] public Guid Wifi { get; init; }
    [JsonPropertyName("uplink_guid")] public Guid Uplink { get; init; }
    [JsonPropertyName("controller_mac")] public int[] ControllerMac { get; init; } = [];
    [JsonPropertyName("channel")] public int Channel { get; init; }
    [JsonPropertyName("ssid")] public string Ssid { get; init; } = "";
    [JsonPropertyName("passphrase")] public string Passphrase { get; init; } = "";
    [JsonIgnore] internal string SourceSha256 { get; private set; } = "";
    internal static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static Configuration Parse(ReadOnlySpan<byte> bytes)
    {
        using (var document = JsonDocument.Parse(bytes.ToArray()))
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new FormatException("Configuration has duplicate fields or an invalid shape.");
        var value = JsonSerializer.Deserialize<Configuration>(bytes, StrictJson) ?? throw new FormatException("Configuration is empty.");
        if (value.ControllerMac is null || value.Ssid is null || value.Passphrase is null || value.Schema != 1 || value.Wifi == Guid.Empty || value.Uplink == Guid.Empty ||
            value.Channel != 36 || value.ControllerMac.Length != 6 || value.ControllerMac.Any(b => b is < 0 or > 255) ||
            value.ControllerMac.All(b => b == 0) || (value.ControllerMac[0] & 1) != 0 ||
            value.Ssid.Length is < 1 or > 32 || !value.Ssid.StartsWith("DIRECT-", StringComparison.Ordinal) ||
            value.Passphrase.Length is < 8 or > 63 || value.Ssid.Any(c => c < 32 || c > 126) || value.Passphrase.Any(c => c < 32 || c > 126))
            throw new FormatException("Invalid current controller configuration.");
        value.SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        return value;
    }
    internal static Configuration Load(string path) => Parse(PrivateFiles.Read(path));
    public override string ToString() => "Configuration(<redacted>)";
}

internal static class PrivateFiles
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA-SHIELD-P2570-Windows");
    internal static string Config => Path.Combine(Root, "config.json");
    internal static string Recovery => Path.Combine(Root, "recovery.json");
    internal static SecurityIdentifier Sid => WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user identity unavailable.");
    internal static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    internal static void RequirePrivate(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Private state cannot be a reparse point.");
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var sid = Sid;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))) || rules.Length == 0 ||
            rules.Any(r => !sid.Equals(r.IdentityReference) || r.AccessControlType != AccessControlType.Allow))
            throw new InvalidOperationException("Controller state must be accessible only to its current user.");
    }
    internal static byte[] Read(string path)
    {
        RequirePrivate(Path.GetDirectoryName(Path.GetFullPath(path))!, true);
        RequirePrivate(path, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > 65536) throw new FormatException("Private state size is invalid.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    internal static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (!Sid.Equals(info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)))) throw new InvalidOperationException("Private directory owner mismatch.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(Sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl); RequirePrivate(path, true);
    }
    internal static void WriteAtomic(string path, object value)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!; RequirePrivate(parent, true);
        string temporary = Path.Combine(parent, ".write-" + Guid.NewGuid().ToString("N"));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true); RequirePrivate(path, false);
    }
}

internal sealed record PackageManifest(string ChildPath, string ChildHash, string OwnerHash, string PairingPath, string PairingHash)
{
    internal static PackageManifest Load()
    {
        string root = PrivateFiles.Root;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "package.json")));
        var r = doc.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1) throw new FormatException("Package schema is not current.");
        string child = Path.Combine(root, "bin", "data-host", "shield-controller-datahost.exe");
        string owner = Path.Combine(root, "bin", "owner", "ShieldControllerOwner.exe");
        string dll = Path.Combine(root, "bin", "owner", "ShieldControllerOwner.dll");
        string pairing = Path.Combine(root, "bin", "pairing", "wcn-pair-worker.exe");
        if (Environment.ProcessPath is not string actual || !PrivateFiles.SamePath(actual, owner)) throw new InvalidOperationException("Run the installed controller owner.");
        string Verify(string path, string field)
        {
            string expected = r.GetProperty(field).GetString() ?? "";
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installed package hash mismatch.");
            return expected.ToUpperInvariant();
        }
        string childHash = Verify(child, "data_host_sha256"), ownerHash = Verify(owner, "owner_exe_sha256");
        Verify(dll, "owner_dll_sha256");
        return new(child, childHash, ownerHash, pairing, Verify(pairing, "pairing_worker_sha256"));
    }
}
