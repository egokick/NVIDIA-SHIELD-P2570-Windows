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
    internal static bool CanProvision(bool selectedRadioOnly, bool uplinkUnchanged, int groupCount, int? channel) =>
        selectedRadioOnly && uplinkUnchanged && groupCount == 1 && (!channel.HasValue || ConnectionSettings.IsPairingChannel(channel.Value));
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
    private static async Task<int?> ReadNativeSetupChannel(Guid expectedGroup)
    {
        Guid[] groups = ChannelInspection.ActiveGroups();
        if (groups.Length != 1) throw new PairingNetworkException(groups.Length == 0 ? PairingNetworkFailureReason.NoGroup : PairingNetworkFailureReason.AmbiguousGroups, groupCount: groups.Length);
        if (groups[0] != expectedGroup) throw new PairingNetworkException(PairingNetworkFailureReason.NetworkChanged, groupCount: groups.Length);
        int? channel;
        try { channel = await Task.Run(() => ChannelInspection.ReadPairing(groups[0])).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { throw new PairingNetworkException(PairingNetworkFailureReason.ChannelQueryTimedOut, groupCount: 1); }
        catch (PairingNetworkException) { throw; }
        catch (InvalidOperationException) { throw new PairingNetworkException(PairingNetworkFailureReason.ChannelUnavailable, groupCount: 1); }
        if (channel.HasValue) ConnectionSettings.RequirePairingChannel(channel, 1);
        return channel;
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
        bool workerStarted = false, workerStopped = false, clean = false, pairSuccess = false, identity = false, connectAttempted = false, provisioningApproved = false, callback = false;
        bool? unknown = false; int? exit = null; string? error = null; JsonElement? complete = null;
        PairingNetworkException? pairingFailure = null;
        Task? outputTask = null, errorTask = null, bandTask = null;
        using var bandCancellation = new CancellationTokenSource();
        try
        {
            checkpoint = "setup-worker-start";
            if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package.PairingPath))).Equals(package.PairingHash, StringComparison.Ordinal)) throw new InvalidOperationException("Pairing worker changed.");
            if (!EmptyHotspot(prepared.Manager.TetheringOperationalState, prepared.Manager.ClientCount) ||
                ChannelInspection.ActiveGroups().Length != 0 || UplinkState() != prepared.UplinkAddressState)
                throw new InvalidOperationException("Setup baseline changed.");
            checkpoint = "setup-worker-start";
            Log(pair ? "pairing-started" : "discovery-started", new { seconds = (int)(pair ? PairingTiming.WorkerPairBudget : PairingTiming.WorkerDiscoveryBudget).TotalSeconds, candidate_credentials_retained = pair, credentials_in_arguments = false, setup_network = "native-wcn" }, true);
            if (!process.Start()) throw new InvalidOperationException("Worker start failed."); workerStarted = true;
            bandTask = Task.Run(() => SetupBandGuard.Monitor(() => process.HasExited, bandCancellation.Token));
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
                        if (groups.Length != 1) throw new PairingNetworkException(groups.Length == 0 ? PairingNetworkFailureReason.NoGroup : PairingNetworkFailureReason.AmbiguousGroups, groupCount: groups.Length);
                        bootstrapChannel = await ReadNativeSetupChannel(groups[0]);
                        var radios = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                        if (!CanProvision(radios.Count == 1 && radios[0].NetworkAdapter.NetworkAdapterId == wifi,
                            UplinkState() == prepared.UplinkAddressState, groups.Length, bootstrapChannel))
                        {
                            checkpoint = "setup-interface-changed";
                            throw new InvalidOperationException("The selected Wi-Fi or Internet adapter changed before provisioning.");
                        }
                        // Recheck after the asynchronous adapter enumeration, just
                        // before the worker is allowed to send credentials.
                        bootstrapChannel = await ReadNativeSetupChannel(groups[0]);
                        bandCancellation.Token.ThrowIfCancellationRequested();
                        Log("provisioning-approved", new { active_groups = groups.Length, bootstrap_channel = bootstrapChannel,
                            channel_confirmed = bootstrapChannel.HasValue, bootstrap_channel_query_error = bootstrapChannel.HasValue ? (int?)null : 5023,
                            input_requires_verified_five_ghz = true });
                        // After approval the worker may provision before its next
                        // log line reaches us. Keep that outcome uncertain until
                        // a terminal worker receipt establishes what happened.
                        provisioningApproved = true; unknown = true;
                        await process.StandardInput.WriteLineAsync("approve-provisioning"); await process.StandardInput.FlushAsync(); process.StandardInput.Close();
                    }
                    if (phase == "connect_begin") { connectAttempted = true; unknown = true; }
                    if (phase == "connect_succeeded") callback = true;
                    if (phase == "complete") complete = value.Clone();
                    // All other worker messages contain only phase/HRESULT/hashed
                    // identifiers and booleans; private candidate rows never pass.
                    Log("pairing-worker", value.Clone());
                }
            });
            errorTask = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) Log("pairing-worker-stderr", new { content_redacted = true }); });
            Task wait = process.WaitForExitAsync();
            Task first = await Task.WhenAny(wait, outputTask, bandTask, Task.Delay(pair ? PairingTiming.WorkerPairBudget : PairingTiming.WorkerDiscoveryBudget));
            if (first == bandTask) await bandTask;
            if (first == outputTask && outputTask.IsFaulted) await outputTask;
            if (!process.HasExited) await wait.WaitAsync(TimeSpan.FromSeconds(first == outputTask ? 2 : 1));
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2));
            await bandTask.WaitAsync(TimeSpan.FromSeconds(3));
            exit = process.ExitCode; workerStopped = true;
            if (complete is JsonElement c)
            {
                identity = c.GetProperty("identity_confirmed").GetBoolean();
                unknown = c.GetProperty("provision_outcome_unknown").GetBoolean();
                pairSuccess = pair && exit == 0 && c.GetProperty("ok").GetBoolean() && c.GetProperty("pairing_success").GetBoolean() && identity && callback && provisioningApproved;
            }
        }
        catch (Exception ex)
        {
            pairingFailure = ex as PairingNetworkException;
            if (pairingFailure is not null) checkpoint = "pairing-network-unavailable";
            error = ex.GetType().Name;
            Log("setup-error", new { checkpoint, type = error, hresult = $"0x{ex.HResult:X8}",
                pairing_network_failure_reason = pairingFailure?.ReasonCode, observed_channel = pairingFailure?.Channel, active_groups = pairingFailure?.GroupCount });
        }
        finally
        {
            // No delayed output callback may approve provisioning during cleanup
            // or after a new attempt starts.
            bandCancellation.Cancel();
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
            try { if (bandTask is not null) await bandTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
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
                // The saved channel is compatibility metadata, never evidence of
                // the pairing channel or authorization for the later input session.
                uplink_guid = candidate.Uplink, controller_mac = candidate.ControllerMac, channel = bootstrapChannel ?? candidate.Channel,
                ssid = candidate.Ssid, passphrase = candidate.Passphrase }));
            PrivateFiles.WriteAtomic(PrivateFiles.Config, candidate!); config = Configuration.Load(PrivateFiles.Config);
            PrivateFiles.WriteAtomic(ProvisionReceipt, new { schema = 1, utc = DateTimeOffset.UtcNow, pairing_success = true, identity_confirmed = true,
                connect_success_callback_observed = true, pc_cleanup_verified = true, config_sha256 = config.SourceSha256, channel = config.Channel,
                bootstrap_channel = bootstrapChannel, channel_confirmed = bootstrapChannel.HasValue, key_representation = "networkKey" });
            Log("pairing-succeeded", new { durable_configuration = true, bootstrap_channel = bootstrapChannel, wireless_input_verified = false }, true);
        }
        else if (okay)
        {
            PrivateFiles.WriteAtomic(Path.Combine(SetupRoot, "discovery.json"), new DiscoveryCache(1, DateTimeOffset.UtcNow, wifi, uplink, targets.Values.ToArray()));
            foreach (var target in targets.Values) Log("candidate", new { target_id_hash = target.TargetIdHash, display_name = "NVIDIA SHIELD P2570 (" + target.TargetIdHash[..8] + ")" }, true);
            Log("discovery-complete", new { candidates = targets.Count, pc_cleanup_verified = clean });
        }
        bool tasksSettled = (outputTask?.IsCompleted ?? true) && (errorTask?.IsCompleted ?? true) && (bandTask?.IsCompleted ?? true);
        PrivateFiles.WriteAtomic(Path.Combine(run, "receipt.json"), new { ok = okay && logFailures == 0, mode, pairing_success = pairSuccess, identity_confirmed = identity,
            setup_network = "native-wcn", worker_tasks_settled = tasksSettled,
            connect_attempted = connectAttempted, connect_success_callback_observed = callback, channel_confirmed = bootstrapChannel.HasValue, provisioning_approved = provisioningApproved, pc_cleanup_verified = clean,
            provision_outcome_unknown = unknown, candidate_credentials_retained = pair, worker_stopped = workerStopped, worker_exit = exit, error_type = error, log_failures = logFailures,
            pairing_network_failure_reason = pairingFailure?.ReasonCode, observed_channel = pairingFailure?.Channel, active_groups = pairingFailure?.GroupCount });
        return okay && logFailures == 0 ? 0 : 1;
    }
}
