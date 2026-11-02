using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Windows.Devices.WiFi;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    private sealed record DiscoveredTarget(string TargetIdHash, int[] ControllerMac);
    private sealed record DiscoveryCache(int Schema, DateTimeOffset Created, Guid Wifi, Guid Uplink, DiscoveredTarget[] Targets);
    private static bool ValidMac(int[]? mac) => mac is { Length: 6 } && mac.All(b => b is >= 0 and <= 255) && mac.Any(b => b != 0) && (mac[0] & 1) == 0;
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static Configuration NewSetupConfiguration(Guid wifi, Guid uplink, int[] mac) => Configuration.Parse(JsonSerializer.SerializeToUtf8Bytes(new {
        schema = 1, wifi_guid = wifi, uplink_guid = uplink, controller_mac = mac, channel = 36,
        ssid = "DIRECT-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) }));
    private static string Profile(Configuration group)
    {
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        XElement E(string name, object content) => new(ns + name, content);
        return new XElement(ns + "WLANProfile", E("name", group.Ssid),
            E("SSIDConfig", E("SSID", E("name", group.Ssid))), E("connectionType", "ESS"), E("connectionMode", "auto"),
            E("MSM", E("security", new object[] { E("authEncryption", new object[] { E("authentication", "WPA2PSK"), E("encryption", "AES"), E("useOneX", "false") }),
                E("sharedKey", new object[] { E("keyType", "networkKey"), E("protected", "false"), E("keyMaterial", DeriveRawPsk(group.Passphrase, group.Ssid)) }) }))).ToString(SaveOptions.DisableFormatting);
    }
    private static async Task<Prepared> PrepareSetup(Guid wifi, Guid uplink, Configuration? candidate)
    {
        if (wifi == Guid.Empty || uplink == Guid.Empty) throw new ArgumentException("Select Wi-Fi and Internet adapters.");
        config = candidate ?? NewSetupConfiguration(wifi, uplink, [2, 0, 0, 0, 0, 1]);
        checkpoint = "setup-single-radio";
        // The WCN SoftAP provider does not expose a selected-adapter constraint.
        // Reject ambiguity instead of claiming it is bound to the chosen adapter.
        var all = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        if (all.Count != 1 || all[0].NetworkAdapter.NetworkAdapterId != wifi) throw new InvalidOperationException("Pairing needs exactly one enabled Wi-Fi adapter.");
        return await Prepare().WaitAsync(TimeSpan.FromSeconds(25));
    }
    private static async Task<int> RecoverSetup()
    {
        using var document = JsonDocument.Parse(PrivateFiles.Read(SetupActive)); var r = document.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1 || LauncherLifetime.MatchesLive(r.GetProperty("owner_pid").GetInt32(), r.GetProperty("owner_creation_filetime").GetUInt64()))
            throw new InvalidOperationException("Setup owner is active or marker invalid.");
        var prepared = await PrepareSetup(r.GetProperty("wifi_guid").GetGuid(), r.GetProperty("uplink_guid").GetGuid(), null);
        if (!EmptyHotspot(prepared.Manager.TetheringOperationalState, prepared.Manager.ClientCount) || ChannelInspection.ActiveGroups().Length != 0)
            throw new InvalidOperationException("Turn off the remaining Windows hotspot before recovery.");
        File.Delete(SetupActive); Log("recovery-complete", new { setup_marker_cleared = true, candidate_credentials_retained = true, radio_mutations = 0 }); return 0;
    }
    private static async Task<int> Setup(string mode, Guid wifi, Guid uplink, string targetHash)
    {
        bool pair = mode == "pair";
        int? bootstrapChannel = null;
        PrivateFiles.RequirePrivate(Root, true); PrivateFiles.CreateDirectory(SetupRoot);
        Configuration? candidate = null; DiscoveredTarget? selected = null;
        string pendingPath = Path.Combine(SetupRoot, "candidate.json");
        if (pair)
        {
            checkpoint = "selected-target";
            if (!ValidHash(targetHash)) throw new ArgumentException("Select a discovered controller.");
            var cache = JsonSerializer.Deserialize<DiscoveryCache>(PrivateFiles.Read(Path.Combine(SetupRoot, "discovery.json")), Configuration.StrictJson)
                ?? throw new FormatException("Discovery cache unavailable.");
            if (cache.Schema != 1 || cache.Wifi != wifi || cache.Uplink != uplink || cache.Created > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - cache.Created > TimeSpan.FromMinutes(15) || cache.Targets is null)
                throw new InvalidOperationException("Discover the controller again.");
            selected = cache.Targets.Single(t => t.TargetIdHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase));
            if (!ValidMac(selected.ControllerMac)) throw new FormatException("Selected target invalid.");
            // Keep the same durable candidate after any uncertain attempt. Never
            // silently overwrite credentials that may already be in the device.
            if (File.Exists(pendingPath)) candidate = Configuration.Load(pendingPath);
            else if (File.Exists(PrivateFiles.Config)) candidate = Configuration.Load(PrivateFiles.Config);
            if (candidate is not null && (candidate.Wifi != wifi || candidate.Uplink != uplink || !candidate.ControllerMac.SequenceEqual(selected.ControllerMac)))
                throw new InvalidOperationException("Existing controller setup differs; remove it explicitly before replacing it.");
            candidate ??= NewSetupConfiguration(wifi, uplink, selected.ControllerMac);
        }
        var prepared = await PrepareSetup(wifi, uplink, candidate);
        if (pair) { PrivateFiles.WriteAtomic(pendingPath, candidate!); candidate = Configuration.Load(pendingPath); }
        string run = Path.Combine(SetupRoot, Guid.NewGuid().ToString("N")); PrivateFiles.CreateDirectory(run); output = run;
        PrivateFiles.WriteAtomic(SetupActive, new { schema = 1, owner_pid = Environment.ProcessId, owner_creation_filetime = OwnerCreationFiletime(), wifi_guid = wifi, uplink_guid = uplink, pair_attempt = pair });
        var targets = new Dictionary<string, DiscoveredTarget>(StringComparer.OrdinalIgnoreCase);
        using var process = new Process { StartInfo = new ProcessStartInfo(package.PairingPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) } };
        process.StartInfo.ArgumentList.Add("--internal-query"); process.StartInfo.ArgumentList.Add(mode);
        bool workerStarted = false, workerStopped = false, clean = false, pairSuccess = false, identity = false, connectAttempted = false, channelApproved = false, callback = false;
        bool? unknown = pair ? true : false; int? exit = null; string? error = null; JsonElement? complete = null;
        Task? outputTask = null, errorTask = null;
        try
        {
            checkpoint = "setup-worker-start";
            if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package.PairingPath))).Equals(package.PairingHash, StringComparison.Ordinal)) throw new InvalidOperationException("Pairing worker changed.");
            if (!EmptyHotspot(prepared.Manager.TetheringOperationalState, prepared.Manager.ClientCount) || ChannelInspection.ActiveGroups().Length != 0 || UplinkState() != prepared.UplinkAddressState)
                throw new InvalidOperationException("Setup baseline changed.");
            Log(pair ? "pairing-started" : "discovery-started", new { seconds = pair ? 90 : 30, candidate_credentials_retained = pair, credentials_in_arguments = false }, true);
            if (!process.Start()) throw new InvalidOperationException("Worker start failed."); workerStarted = true;
            process.StandardInput.NewLine = "\n";
            if (pair)
            {
                await process.StandardInput.WriteLineAsync(Convert.ToHexString(selected!.ControllerMac.Select(b => (byte)b).ToArray()));
                await process.StandardInput.WriteLineAsync(Profile(candidate!)); await process.StandardInput.FlushAsync();
            }
            else process.StandardInput.Close();
            outputTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is string row)
                {
                    if (row.Length > 32768) throw new FormatException("Worker output too large.");
                    using var document = JsonDocument.Parse(row); var value = document.RootElement; string phase = value.GetProperty("phase").GetString() ?? "";
                    if (phase == "private_candidate")
                    {
                        string hash = value.GetProperty("target_id_hash").GetString() ?? ""; int[] mac = value.GetProperty("controller_mac").EnumerateArray().Select(b => b.GetInt32()).ToArray();
                        if (pair || !ValidHash(hash) || !ValidMac(mac) || !value.GetProperty("model_verified").GetBoolean() || !value.GetProperty("manufacturer_verified").GetBoolean()) throw new FormatException("Candidate identity rejected.");
                        if (targets.Count >= 64 && !targets.ContainsKey(hash)) throw new FormatException("Too many candidates.");
                        targets[hash] = new(hash, mac); // Raw address stays in this private cache only.
                        continue;
                    }
                    if (phase == "provision_ready")
                    {
                        if (!pair) throw new FormatException("Unexpected provision request.");
                        checkpoint = "pairing-bootstrap-channel";
                        Guid[] groups = ChannelInspection.ActiveGroups();
                        if (groups.Length == 1) bootstrapChannel = await Task.Run(() => ChannelInspection.Read(groups[0])).WaitAsync(TimeSpan.FromSeconds(2));
                        if (!bootstrapChannel.HasValue || !ConnectionSettings.ValidChannel(bootstrapChannel.Value) || UplinkState() != prepared.UplinkAddressState)
                            throw new InvalidOperationException("WCN bootstrap must use a supported controller channel and the selected interfaces.");
                        channelApproved = true; await process.StandardInput.WriteLineAsync("approve-bootstrap-channel"); await process.StandardInput.FlushAsync(); process.StandardInput.Close();
                    }
                    if (phase == "connect_begin") connectAttempted = true;
                    if (phase == "connect_succeeded") callback = true;
                    if (phase == "complete") complete = value.Clone();
                    // All other worker messages contain only phase/HRESULT/hashed
                    // identifiers and booleans; private candidate rows never pass.
                    Log("pairing-worker", value.Clone());
                }
            });
            errorTask = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) Log("pairing-worker-stderr", new { content_redacted = true }); });
            Task wait = process.WaitForExitAsync();
            Task first = await Task.WhenAny(wait, outputTask, Task.Delay(TimeSpan.FromSeconds(pair ? 90 : 30)));
            if (first == outputTask && outputTask.IsFaulted) await outputTask;
            if (!process.HasExited) await wait.WaitAsync(TimeSpan.FromSeconds(first == outputTask ? 2 : 1));
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2));
            exit = process.ExitCode; workerStopped = true;
            if (complete is JsonElement c)
            {
                identity = c.GetProperty("identity_confirmed").GetBoolean();
                unknown = c.GetProperty("provision_outcome_unknown").GetBoolean();
                pairSuccess = pair && exit == 0 && c.GetProperty("ok").GetBoolean() && c.GetProperty("pairing_success").GetBoolean() && identity && callback && channelApproved;
            }
        }
        catch (Exception ex) { error = ex.GetType().Name; Log("setup-error", new { checkpoint, type = error, hresult = $"0x{ex.HResult:X8}" }); }
        finally
        {
            if (workerStarted)
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.HasExited)
                {
                    // Worker exit is not a WPS rollback. Keep the durable candidate.
                    try { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                }
                workerStopped = process.HasExited;
                try { if (outputTask is not null) await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                try { if (errorTask is not null) await errorTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            else workerStopped = true;
            var until = DateTimeOffset.UtcNow.AddSeconds(8);
            do
            {
                try { clean = workerStopped && EmptyHotspot(prepared.Manager.TetheringOperationalState, prepared.Manager.ClientCount) && ChannelInspection.ActiveGroups().Length == 0 &&
                    UplinkState() == prepared.UplinkAddressState && prepared.Original.Same(prepared.Manager.GetCurrentAccessPointConfiguration()) &&
                    StartupModes().SequenceEqual(prepared.StartupModes) && NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled() == prepared.OriginalIdleTimeout; }
                catch { clean = false; }
                if (clean) break; await Task.Delay(250);
            } while (DateTimeOffset.UtcNow < until);
            if (clean) File.Delete(SetupActive);
        }
        bool workerOkay = complete is JsonElement done && done.GetProperty("ok").GetBoolean() && exit == 0;
        bool okay = workerOkay && clean && error is null && logFailures == 0 && (!pair || pairSuccess);
        if (okay && pair)
        {
            checkpoint = "persist-pairing";
            candidate = Configuration.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schema = candidate!.Schema, wifi_guid = candidate.Wifi,
                uplink_guid = candidate.Uplink, controller_mac = candidate.ControllerMac, channel = bootstrapChannel!.Value,
                ssid = candidate.Ssid, passphrase = candidate.Passphrase }));
            PrivateFiles.WriteAtomic(PrivateFiles.Config, candidate!); config = Configuration.Load(PrivateFiles.Config);
            PrivateFiles.WriteAtomic(ProvisionReceipt, new { schema = 1, utc = DateTimeOffset.UtcNow, pairing_success = true, identity_confirmed = true,
                connect_success_callback_observed = true, pc_cleanup_verified = true, config_sha256 = config.SourceSha256, channel = config.Channel, bootstrap_channel = bootstrapChannel, key_representation = "networkKey" });
            Log("pairing-succeeded", new { durable_configuration = true, bootstrap_channel = bootstrapChannel, wireless_input_verified = false }, true);
        }
        else if (okay)
        {
            PrivateFiles.WriteAtomic(Path.Combine(SetupRoot, "discovery.json"), new DiscoveryCache(1, DateTimeOffset.UtcNow, wifi, uplink, targets.Values.ToArray()));
            foreach (var target in targets.Values) Log("candidate", new { target_id_hash = target.TargetIdHash, display_name = "NVIDIA SHIELD P2570 (" + target.TargetIdHash[..8] + ")" }, true);
            Log("discovery-complete", new { candidates = targets.Count, pc_cleanup_verified = clean });
        }
        PrivateFiles.WriteAtomic(Path.Combine(run, "receipt.json"), new { ok = okay && logFailures == 0, mode, pairing_success = pairSuccess, identity_confirmed = identity,
            connect_attempted = connectAttempted, connect_success_callback_observed = callback, channel_confirmed = channelApproved, pc_cleanup_verified = clean,
            provision_outcome_unknown = unknown, candidate_credentials_retained = pair, worker_stopped = workerStopped, worker_exit = exit, error_type = error, log_failures = logFailures });
        return okay && logFailures == 0 ? 0 : 1;
    }
}
