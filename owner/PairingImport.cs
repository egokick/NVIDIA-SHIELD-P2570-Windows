using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Importing an existing pairing is distinct from observing WCN succeed here.
// The export contains the original passphrase, not the derived 64-hex WPA2 PSK.
internal sealed record PairingExport
{
    [JsonPropertyName("format")] public string Format { get; init; } = "";
    [JsonPropertyName("exported_utc")] public DateTimeOffset Exported { get; init; }
    [JsonPropertyName("source_config_sha256")] public string SourceConfigHash { get; init; } = "";
    [JsonPropertyName("pairing_utc")] public DateTimeOffset Paired { get; init; }
    [JsonPropertyName("controller_mac")] public int[] ControllerMac { get; init; } = [];
    [JsonPropertyName("ssid")] public string Ssid { get; init; } = "";
    [JsonPropertyName("passphrase")] public string Passphrase { get; init; } = "";
    [JsonPropertyName("channel")] public int Channel { get; init; }
    [JsonPropertyName("pairing_success")] public bool PairingSuccess { get; init; }
    [JsonPropertyName("identity_confirmed")] public bool IdentityConfirmed { get; init; }
    [JsonPropertyName("source_adapter_guids_omitted")] public bool AdapterGuidsOmitted { get; init; }

    internal static PairingExport Parse(byte[] bytes, DateTimeOffset now)
    {
        PairingImportJson.RequireObject(bytes);
        var value = JsonSerializer.Deserialize<PairingExport>(bytes, Configuration.StrictJson) ?? throw new FormatException("Pairing export is empty.");
        if (value.Format != "shield-p2570-private-pairing-export-v1" || !value.PairingSuccess || !value.IdentityConfirmed || !value.AdapterGuidsOmitted ||
            !PairingImportJson.HashValid(value.SourceConfigHash) || !PairingImportJson.DatesValid(value.Paired, value.Exported, now) ||
            !ConnectionSettings.IsFiveGhzChannel(value.Channel)) throw new FormatException("Pairing export verification metadata is invalid.");
        // Reuse the same credential and physical-identity validation as runtime.
        _ = value.Bind(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
        return value;
    }
    internal Configuration Bind(Guid wifi, Guid uplink) => Configuration.Parse(JsonSerializer.SerializeToUtf8Bytes(new {
        schema = 1, wifi_guid = wifi, uplink_guid = uplink, controller_mac = ControllerMac,
        channel = Channel, ssid = Ssid, passphrase = Passphrase }));
    public override string ToString() => "PairingExport(<redacted>)";
}

internal sealed record ImportedPairingReceipt
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 2;
    [JsonRequired, JsonPropertyName("method")] public string Method { get; init; } = "imported-existing-pairing";
    [JsonPropertyName("imported_utc")] public DateTimeOffset Imported { get; init; }
    [JsonPropertyName("config_sha256")] public string ConfigHash { get; init; } = "";
    [JsonPropertyName("channel")] public int Channel { get; init; }
    [JsonPropertyName("source_export_sha256")] public string ExportHash { get; init; } = "";
    [JsonPropertyName("source_config_sha256")] public string SourceConfigHash { get; init; } = "";
    [JsonPropertyName("source_exported_utc")] public DateTimeOffset Exported { get; init; }
    [JsonPropertyName("source_pairing_utc")] public DateTimeOffset Paired { get; init; }
    [JsonPropertyName("source_pairing_success")] public bool SourcePairingSuccess { get; init; }
    [JsonPropertyName("source_identity_confirmed")] public bool SourceIdentityConfirmed { get; init; }
    [JsonPropertyName("source_adapter_guids_omitted")] public bool SourceAdapterGuidsOmitted { get; init; }
    [JsonRequired, JsonPropertyName("local_pairing_performed")] public bool LocalPairingPerformed { get; init; }
    [JsonRequired, JsonPropertyName("local_wireless_input_verified")] public bool LocalWirelessInputVerified { get; init; }

    internal static ImportedPairingReceipt Create(PairingExport source, string exportHash, Configuration bound, DateTimeOffset now) => new() {
        Imported = now, ConfigHash = bound.SourceSha256, Channel = bound.Channel, ExportHash = exportHash,
        SourceConfigHash = source.SourceConfigHash, Exported = source.Exported, Paired = source.Paired,
        SourcePairingSuccess = source.PairingSuccess, SourceIdentityConfirmed = source.IdentityConfirmed,
        SourceAdapterGuidsOmitted = source.AdapterGuidsOmitted,
    };
    internal static bool Matches(byte[] bytes, Configuration bound, DateTimeOffset now)
    {
        try
        {
            PairingImportJson.RequireObject(bytes);
            var value = JsonSerializer.Deserialize<ImportedPairingReceipt>(bytes, Configuration.StrictJson);
            return value is not null && value.Schema == 2 && value.Method == "imported-existing-pairing" &&
                value.SourcePairingSuccess && value.SourceIdentityConfirmed && value.SourceAdapterGuidsOmitted &&
                !value.LocalPairingPerformed && !value.LocalWirelessInputVerified &&
                PairingImportJson.DatesValid(value.Paired, value.Exported, now) && value.Imported >= value.Paired &&
                value.Imported <= now.AddMinutes(5) && PairingImportJson.HashValid(value.ExportHash) &&
                PairingImportJson.HashValid(value.SourceConfigHash) && PairingImportJson.HashValid(value.ConfigHash) &&
                value.ConfigHash.Equals(bound.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
                value.Channel == bound.Channel && ConnectionSettings.IsFiveGhzChannel(value.Channel);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return false; }
    }
}

internal static class PairingImportJson
{
    internal static bool HashValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool DatesValid(DateTimeOffset paired, DateTimeOffset exported, DateTimeOffset now) =>
        paired > DateTimeOffset.UnixEpoch && exported >= paired && exported <= now.AddMinutes(5);
    internal static void RequireObject(byte[] bytes)
    {
        if (bytes.Length is < 2 or > 65536) throw new FormatException("Pairing import state size is invalid.");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new FormatException("Pairing import state has duplicate fields or an invalid shape.");
    }
}

internal static partial class Program
{
    internal static bool SamePairing(Configuration first, Configuration second) =>
        first.ControllerMac.SequenceEqual(second.ControllerMac) && first.Ssid == second.Ssid && first.Passphrase == second.Passphrase;

    private static void WriteImportBytes(string path, byte[] bytes)
    {
        string parent = Path.GetDirectoryName(path)!; PrivateFiles.RequirePrivate(parent, true);
        if (File.Exists(path)) PrivateFiles.RequirePrivate(path, false);
        string temporary = Path.Combine(parent, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            PrivateFiles.RequirePrivate(temporary, false);
            File.Move(temporary, path, true); PrivateFiles.RequirePrivate(path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Commit both files or put their original exact bytes back. A process crash
    // between writes leaves a hash mismatch, which cannot authorize a session.
    internal static void CommitPairingImport(byte[] configuration, byte[] receipt, byte[]? oldConfig, byte[]? oldReceipt,
        Action<bool, byte[]?> write)
    {
        try { write(false, configuration); write(true, receipt); }
        catch
        {
            Exception? failure = null;
            try { write(false, oldConfig); } catch (Exception error) { failure = error; }
            try { write(true, oldReceipt); } catch (Exception error) { failure ??= error; }
            if (failure is not null) throw new IOException("Pairing import failed and previous state could not be completely restored.", failure);
            throw;
        }
    }

    private static int ImportPairing(string sourcePath, Guid wifi, Guid uplink)
    {
        checkpoint = "pairing-import";
        if (File.Exists(PrivateFiles.Recovery) || File.Exists(SetupActive)) throw new InvalidOperationException("Resolve controller cleanup before importing pairing.");
        var localAdapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => Guid.TryParse(adapter.Id, out _)).Select(adapter => Guid.Parse(adapter.Id)).ToHashSet();
        if (!localAdapters.Contains(wifi) || !localAdapters.Contains(uplink)) throw new InvalidOperationException("Select adapters belonging to this PC.");
        string source = Path.GetFullPath(sourcePath);
        string prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || PrivateFiles.SamePath(source, PrivateFiles.Config) || PrivateFiles.SamePath(source, ProvisionReceipt))
            throw new InvalidOperationException("Stage the export in a private directory inside the installed app root.");
        for (string? parent = Path.GetDirectoryName(source); parent is not null && parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            PrivateFiles.RequirePrivate(parent, true);
        byte[] sourceBytes = PrivateFiles.Read(source);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PairingExport exported = PairingExport.Parse(sourceBytes, now);
        Configuration imported = exported.Bind(wifi, uplink);
        string pendingPath = Path.Combine(SetupRoot, "candidate.json");
        byte[]? oldConfig = File.Exists(PrivateFiles.Config) ? PrivateFiles.Read(PrivateFiles.Config) : null;
        byte[]? oldReceipt = File.Exists(ProvisionReceipt) ? PrivateFiles.Read(ProvisionReceipt) : null;
        if (oldConfig is not null && !SamePairing(Configuration.Parse(oldConfig), imported)) throw new InvalidOperationException("Existing controller pairing differs; remove it explicitly before replacing it.");
        if (File.Exists(pendingPath) && !SamePairing(Configuration.Load(pendingPath), imported)) throw new InvalidOperationException("An unfinished controller pairing differs; its credentials were preserved.");
        // Neither source claims nor old session receipts bypass runtime checks.
        byte[] configBytes = JsonSerializer.SerializeToUtf8Bytes(imported, new JsonSerializerOptions { WriteIndented = true });
        imported = Configuration.Parse(configBytes);
        string exportHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(ImportedPairingReceipt.Create(exported, exportHash, imported, now), new JsonSerializerOptions { WriteIndented = true });
        if (!ImportedPairingReceipt.Matches(receiptBytes, imported, now)) throw new InvalidOperationException("Imported pairing provenance did not validate.");
        CommitPairingImport(configBytes, receiptBytes, oldConfig, oldReceipt, (isReceipt, bytes) => {
            string destination = isReceipt ? ProvisionReceipt : PrivateFiles.Config;
            if (bytes is not null) WriteImportBytes(destination, bytes);
            else if (File.Exists(destination)) { PrivateFiles.RequirePrivate(destination, false); File.Delete(destination); }
        });
        Log("pairing-imported", new { existing_pairing_saved = true, source_pairing_success_reported = true,
            source_identity_confirmed_reported = true, local_pairing_performed = false,
            wireless_input_verified = false, local_adapters_bound = true, radio_mutations = 0 }, true);
        return 0;
    }

    private static int PairingImportSelfTest()
    {
        int count = 0;
        void Check(bool value) { count++; if (!value) throw new InvalidOperationException("Pairing import contract check failed."); }
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-02T22:00:00Z");
        var source = new PairingExport { Format = "shield-p2570-private-pairing-export-v1", Exported = now.AddMinutes(-1), Paired = now.AddDays(-1),
            SourceConfigHash = new string('A', 64), ControllerMac = [2, 1, 2, 3, 4, 5], Ssid = "DIRECT-TEST", Passphrase = "SyntheticOnly123",
            Channel = 36, PairingSuccess = true, IdentityConfirmed = true, AdapterGuidsOmitted = true };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(source);
        var parsed = PairingExport.Parse(bytes, now);
        var local = parsed.Bind(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Check(local.Ssid == source.Ssid && local.Passphrase == source.Passphrase && local.ControllerMac.SequenceEqual(source.ControllerMac));
        Check(parsed.ToString() == "PairingExport(<redacted>)");
        foreach (var invalid in new[] { source with { PairingSuccess = false }, source with { IdentityConfirmed = false }, source with { AdapterGuidsOmitted = false },
            source with { SourceConfigHash = "invalid" }, source with { Channel = 11 }, source with { Exported = now.AddMinutes(10) }, source with { Paired = now },
            source with { Passphrase = new string('A', 64) }, source with { ControllerMac = [3, 1, 2, 3, 4, 5] } })
        { bool rejected = false; try { PairingExport.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid), now); } catch (FormatException) { rejected = true; } Check(rejected); }
        string json = Encoding.UTF8.GetString(bytes);
        foreach (string invalid in new[] { json.Replace("\"channel\":36", "\"channel\":36,\"channel\":36"), json.Replace("\"channel\":36", "\"channel\":36,\"unknown\":true") })
        { bool rejected = false; try { PairingExport.Parse(Encoding.UTF8.GetBytes(invalid), now); } catch (Exception error) when (error is FormatException or JsonException) { rejected = true; } Check(rejected); }
        var receipt = ImportedPairingReceipt.Create(parsed, Convert.ToHexString(SHA256.HashData(bytes)), local, now);
        Check(ImportedPairingReceipt.Matches(JsonSerializer.SerializeToUtf8Bytes(receipt), local, now));
        string receiptJson = JsonSerializer.Serialize(receipt);
        Check(!ImportedPairingReceipt.Matches(Encoding.UTF8.GetBytes(receiptJson.Replace("\"method\":\"imported-existing-pairing\",", "")), local, now));
        Check(!ImportedPairingReceipt.Matches(Encoding.UTF8.GetBytes(receiptJson.Replace(",\"local_pairing_performed\":false", "")), local, now));
        Check(!ImportedPairingReceipt.Matches(Encoding.UTF8.GetBytes(receiptJson.Replace(",\"local_wireless_input_verified\":false", "")), local, now));
        foreach (var invalid in new[] { receipt with { ConfigHash = new string('B', 64) }, receipt with { ExportHash = "bad" }, receipt with { LocalPairingPerformed = true },
            receipt with { LocalWirelessInputVerified = true }, receipt with { SourceIdentityConfirmed = false }, receipt with { Channel = 11 }, receipt with { Method = "wcn" } })
            Check(!ImportedPairingReceipt.Matches(JsonSerializer.SerializeToUtf8Bytes(invalid), local, now));
        var different = Configuration.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, wifi_guid = local.Wifi, uplink_guid = local.Uplink,
            controller_mac = local.ControllerMac, channel = 36, ssid = local.Ssid, passphrase = "AnotherSyntheticKey" }));
        Check(SamePairing(local, local) && !SamePairing(local, different));
        byte[]? savedConfig = [1], savedReceipt = [2]; bool injected = false, failed = false;
        try { CommitPairingImport([3], [4], savedConfig, savedReceipt, (isReceipt, data) => {
            if (isReceipt && !injected) { injected = true; throw new IOException("Synthetic receipt failure."); }
            if (isReceipt) savedReceipt = data; else savedConfig = data;
        }); } catch (IOException) { failed = true; }
        Check(failed && savedConfig!.SequenceEqual(new byte[] { 1 }) && savedReceipt!.SequenceEqual(new byte[] { 2 }));
        savedConfig = savedReceipt = null; injected = failed = false;
        try { CommitPairingImport([3], [4], null, null, (isReceipt, data) => {
            if (isReceipt && !injected) { injected = true; throw new IOException("Synthetic receipt failure."); }
            if (isReceipt) savedReceipt = data; else savedConfig = data;
        }); } catch (IOException) { failed = true; }
        Check(failed && savedConfig is null && savedReceipt is null);
        return count;
    }
}
