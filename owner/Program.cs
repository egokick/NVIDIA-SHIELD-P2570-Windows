// Owns one configured SHIELD wireless session and restores its PC resources.
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Windows.Devices.WiFi;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    private static string Root => PrivateFiles.Root;
    private static Configuration config = null!;
    private static PackageManifest package = null!;
    private static LauncherLifetime? launcher;
    private static RecoveryState? recovery;
    private static HostResult? currentHost;
    private static string sessionId = "";
    private static Guid Uplink => config.Uplink;
    private static Guid Wifi => config.Wifi;
    private static string ChildPath => package.ChildPath;
    private static string ChildHash => package.ChildHash;
    private static string? output;
    private static int logFailures;
    private static string checkpoint = "argument-validation";
    private static readonly object logGate = new();
    private static bool loggingClosed;
    private static int progressMirrorFailures;
    private static bool StopRequested(string? directory) => directory is not null && File.Exists(Path.Combine(directory, "request-stop"));
    private static bool ShouldStop => StopRequested(output) || launcher?.Alive == false;
    private static bool Log(string phase, object detail, bool required = false)
    { lock (logGate) return loggingClosed || LogCore(phase, detail, required); }
    private static bool LogCore(string phase, object detail, bool required)
    {
        string json = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, phase,
            transport_ready = currentHost?.TransportReady == true && currentHost?.Stopped != true, connected = currentHost?.XinputSlot is not null && currentHost?.Stopped != true,
            controller_detected = currentHost?.TransportReady == true && currentHost?.ExpectedControllerSession == true && currentHost?.Stopped != true,
            xinput_slot = currentHost?.XinputSlot,
            firmware_version = currentHost?.Stopped != true ? currentHost?.FirmwareVersion : null, detail });
        bool success = true;
        try { Console.WriteLine(json); } catch { success = false; }
        if (output is not null)
        {
            try { BoundedHistory.Append(Path.Combine(output, "events.jsonl"), json); }
            catch (Exception ex)
            {
                success = false;
                try { Console.WriteLine(JsonSerializer.Serialize(new { phase = "log-error", stage = "events-append", type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" })); } catch { }
            }
            string stage = "status-write";
            try
            {
                File.WriteAllText(Path.Combine(output, "status.tmp"), json);
                stage = "status-replace";
                File.Move(Path.Combine(output, "status.tmp"), Path.Combine(output, "status.json"), true);
            }
            catch (Exception ex)
            {
                progressMirrorFailures++;
                try { Console.WriteLine(JsonSerializer.Serialize(new { phase = "status-mirror-error", stage, type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" })); } catch { success = false; }
            }
        }
        if (!success) { logFailures++; if (required) throw new IOException("Required controller event could not be persisted."); }
        return success;
    }

    private sealed class PersistentConfig
    {
        private readonly string ssid, password;
        internal TetheringWiFiBand Band { get; }
        internal TetheringWiFiAuthenticationKind Authentication { get; }
        internal PersistentConfig(NetworkOperatorTetheringAccessPointConfiguration c)
        { ssid = c.Ssid; password = c.Passphrase; Band = c.Band; Authentication = c.AuthenticationKind; }
        internal bool Same(NetworkOperatorTetheringAccessPointConfiguration c) => ssid == c.Ssid && password == c.Passphrase && Band == c.Band && Authentication == c.AuthenticationKind;
    }
    private sealed record Prepared(NetworkOperatorTetheringManager Manager, Configuration Group, PersistentConfig Original, string UplinkAddressState, int[] StartupModes, bool OriginalIdleTimeout, int Band);
    internal static bool OwnsSuccessfulStart(TetheringOperationStatus status) => status == TetheringOperationStatus.Success;
    internal static bool EmptyHotspot(TetheringOperationalState state, uint clients) => state == TetheringOperationalState.Off && clients == 0;
    private sealed record Settlement<T>(bool HasResult, T? Result, bool Pending, bool CancelCallFailed);
    private static async Task<Settlement<T>> CancelAndSettle<T>(Action cancel, Task<T> task, TimeSpan timeout)
    {
        bool cancelFailed = false;
        try { cancel(); } catch { cancelFailed = true; }
        // Cancel itself may throw. The original operation must still be tracked.
        try { return new(true, await task.WaitAsync(timeout), false, cancelFailed); }
        catch
        {
            // Take one status snapshot: a timeout racing a successful completion
            // must not lose ownership of a hotspot that actually started.
            TaskStatus status = task.Status;
            if (status == TaskStatus.RanToCompletion) return new(true, task.GetAwaiter().GetResult(), false, cancelFailed);
            return new(false, default, status is not (TaskStatus.Canceled or TaskStatus.Faulted), cancelFailed);
        }
    }

    private static string UplinkState()
    {
        var current = SelectedUplink();
        if (current?.NetworkAdapter?.NetworkAdapterId != Uplink || current.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
            throw new InvalidOperationException("Expected Uplink internet profile unavailable.");
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => Guid.TryParse(n.Id, out var id) && id == Uplink);
        if (nic.OperationalStatus != OperationalStatus.Up) throw new InvalidOperationException("Uplink is not up.");
        var p = nic.GetIPProperties();
        // Compared only in memory; no addresses, gateways, DNS or profile names logged.
        return string.Join('|', p.UnicastAddresses.Select(a => a.Address.ToString()).Order()) + ";" +
            string.Join('|', p.GatewayAddresses.Select(a => a.Address.ToString()).Order()) + ";" +
            string.Join('|', p.DnsAddresses.Select(a => a.ToString()).Order());
    }
    private static ConnectionProfile? SelectedUplink() => NetworkInformation.GetConnectionProfiles()
        .FirstOrDefault(p => p.NetworkAdapter?.NetworkAdapterId == Uplink && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess);
    private static int[] StartupModes() => new[] { "SharedAccess", "icssvc", "WlanSvc" }.Select(name =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, false);
        return key?.GetValue("Start") is int start ? start : throw new InvalidOperationException("Service startup metadata unavailable.");
    }).ToArray();

    private static async Task<Prepared> Prepare()
    {
        checkpoint = "read-only-preflight";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new InvalidOperationException("Windows 11 24H2 or later on an Intel or AMD x64 PC is required.");
        if (!ApiInformation.IsTypePresent("Windows.Networking.NetworkOperators.NetworkOperatorTetheringSessionAccessPointConfiguration") ||
            !ApiInformation.IsMethodPresent("Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager", "StartTetheringAsync", 1))
            throw new InvalidOperationException("Per-session API unavailable.");
        foreach (string method in new[] { "IsNoConnectionsTimeoutEnabled", "DisableNoConnectionsTimeoutAsync", "EnableNoConnectionsTimeoutAsync" })
            if (!ApiInformation.IsMethodPresent("Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager", method))
                throw new InvalidOperationException("Documented idle-timeout API unavailable.");
        bool originalIdleTimeout = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
        string ethernet = UplinkState();
        var internet = SelectedUplink();
        if (NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(internet) != TetheringCapability.Enabled)
            throw new InvalidOperationException("Tethering capability disabled.");
        var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        var selectedAdapters = adapters.Where(a => a.NetworkAdapter.NetworkAdapterId == Wifi).ToArray();
        if (selectedAdapters.Length != 1) throw new InvalidOperationException("Selected Wi-Fi adapter is unavailable.");
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(internet, selectedAdapters[0].NetworkAdapter);
        if (!EmptyHotspot(manager.TetheringOperationalState, manager.ClientCount) || ChannelInspection.ActiveGroups().Length != 0)
            throw new InvalidOperationException("Pre-existing hotspot, clients or GO rejected.");
        var apConfig = manager.GetCurrentAccessPointConfiguration();
        bool five = await apConfig.IsBandSupportedAsync(TetheringWiFiBand.FiveGigahertz).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        int band = ConnectionSettings.Select(five);
        if (!await apConfig.IsAuthenticationKindSupportedAsync(TetheringWiFiAuthenticationKind.Wpa2).AsTask().WaitAsync(TimeSpan.FromSeconds(8)))
            throw new InvalidOperationException("WPA2 unsupported.");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ChildPath))) != ChildHash) throw new InvalidOperationException("Data host changed.");
        var group = config;
        OwnerExecutable();
        OwnerCreationFiletime();
        var original = new PersistentConfig(apConfig);
        int[] starts = StartupModes();
        Log("validated", new { exact_uplink = true, exact_selected_wifi = true, internet_access = true,
            hotspot = manager.TetheringOperationalState.ToString(), clients = manager.ClientCount,
            original_band = original.Band.ToString(), original_authentication = original.Authentication.ToString(),
            original_no_connections_timeout_enabled = originalIdleTimeout, idle_policy_setter_called = false,
            supports_5ghz = five, selected_band = band, supports_wpa2 = true, protected_group_valid = true,
            data_host_pin_valid = true, current_configuration_valid = true, service_startup_modes = starts,
            persistent_settings_written = false, ics_snapshot_available = false,
            ics_note = "Mobile Hotspot temporarily uses internet sharing; no global ICS configuration or service setter is called." }, required: true);
        return new(manager, group, original, ethernet, starts, originalIdleTimeout, band);
    }

    private static async Task<int> Run(Prepared prepared, CancellationToken cancellation)
    {
        var manager = prepared.Manager;
        if (ShouldStop)
        {
            var early = new { ok = true, started = false, stop_requested = true, request_file = "request-stop", ap_mutation_calls = 0, log_failures = logFailures };
            Log("stop-requested-before-start", early);
            early = early with { ok = logFailures == 0, log_failures = logFailures };
            File.WriteAllText(Path.Combine(output!, "receipt.json"), JsonSerializer.Serialize(early));
            return logFailures == 0 ? 0 : 1;
        }
        checkpoint = "immediate-pre-start";
        if (!EmptyHotspot(manager.TetheringOperationalState, manager.ClientCount) || ChannelInspection.ActiveGroups().Length != 0 || UplinkState() != prepared.UplinkAddressState || NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled() != prepared.OriginalIdleTimeout)
            throw new InvalidOperationException("Immediate pre-start state changed.");
        Log("starting-temporary-hotspot", new { requested_band = prepared.Band, lifetime = "Until launcher exits or Stop is requested", ownership = "Exclusive controlled window; Windows exposes no session lease token.",
            persistent_configuration_setter_used = false }, required: true);
        var sessionConfig = new NetworkOperatorTetheringSessionAccessPointConfiguration {
            Ssid = prepared.Group.Ssid, Passphrase = prepared.Group.Passphrase,
            Band = TetheringWiFiBand.FiveGigahertz, AuthenticationKind = TetheringWiFiAuthenticationKind.Wpa2,
            PerformancePriority = TetheringWiFiPerformancePriority.Default,
        };
        Stopwatch watch = Stopwatch.StartNew();
        bool owns = false, started = false, observed = false, stopped = false, off = false, ethernetOk = false, persistentSame = false, startupSame = false;
        bool startUnsettled = false, stopUnsettled = false;
        bool finalOutcome = false;
        var idlePolicy = new IdleTimeoutPolicy(prepared.OriginalIdleTimeout);
        var hostResult = new HostResult(); currentHost = hostResult; Guid? groupId = null;
        int? channel = null;
        string? errorType = null, failureCheckpoint = null, failureHotspotState = null, startStatus = null, stopStatus = null;
        IAsyncOperation<NetworkOperatorTetheringOperationResult>? startOperation = null;
        Task<NetworkOperatorTetheringOperationResult>? startTask = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            recovery = new RecoveryState { SessionId = sessionId, ConfigHash = config.SourceSha256,
                OwnerPid = Environment.ProcessId, OwnerCreation = OwnerCreationFiletime(), OriginalIdleTimeout = prepared.OriginalIdleTimeout };
            recovery.Save();
            checkpoint = "start-hotspot";
            NetworkOperatorTetheringOperationResult result;
            startOperation = manager.StartTetheringAsync(sessionConfig);
            startTask = startOperation.AsTask();
            try { result = await startTask.WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
            catch
            {
                var late = await CancelAndSettle(() => startOperation.Cancel(), startTask, TimeSpan.FromSeconds(10));
                startUnsettled = late.Pending;
                if (late.HasResult)
                {
                    startStatus = late.Result!.Status.ToString();
                    owns = OwnsSuccessfulStart(late.Result.Status);
                }
                Log("start-cancel-settlement", new { pending = late.Pending, cancel_call_failed = late.CancelCallFailed, start_status = startStatus });
                throw;
            }
            startStatus = result.Status.ToString();
            owns = OwnsSuccessfulStart(result.Status); // AlreadyOn never establishes ownership.
            if (!owns) throw new InvalidOperationException("Windows could not start the requested controller hotspot: " + startStatus);
            started = true;
            recovery.OwnedStart = true; recovery.Save();
            Log("hotspot-started", new { status = startStatus, elapsed_seconds = watch.Elapsed.TotalSeconds });
            checkpoint = "verify-channel";
            var deadline = watch.Elapsed + TimeSpan.FromSeconds(5);
            while (watch.Elapsed < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                var groups = ChannelInspection.ActiveGroups();
                if (groups.Length > 1) throw new FiveGhzRequiredException(FiveGhzFailureReason.AmbiguousGroups, groupCount: groups.Length);
                if (groups.Length == 1)
                {
                    try { channel = await Task.Run(() => ChannelInspection.Read(groups[0])).WaitAsync(TimeSpan.FromSeconds(2), cancellation); }
                    catch (InvalidOperationException) { }
                    catch (TimeoutException) { throw new FiveGhzRequiredException(FiveGhzFailureReason.ChannelQueryTimedOut, groupCount: groups.Length); }
                    if (channel.HasValue) { groupId = groups[0]; break; }
                }
                await Task.Delay(250, cancellation);
            }
            int verifiedChannel = channel ?? throw new FiveGhzRequiredException(FiveGhzFailureReason.ChannelUnavailable);
            ConnectionSettings.RequireFiveGhzChannel(verifiedChannel, 1);
            if (!await Task.Run(() => ChannelInspection.ReadSsidEquals(groupId!.Value, config.Ssid)).WaitAsync(TimeSpan.FromSeconds(2), cancellation))
                throw new InvalidOperationException("Active hotspot SSID differs from current configuration.");
            Log("channel-verified", new { channel, actual_requested_band = prepared.Band, actual_ssid_matches_configuration = true, hotspot = manager.TetheringOperationalState.ToString() });
            checkpoint = "data-host-session";
            recovery.Channel = verifiedChannel; recovery.GroupId = groupId; recovery.GroupMac = GroupMac(groupId!.Value).Select(b => (int)b).ToArray();
            recovery.IdleDisablePlanned = prepared.OriginalIdleTimeout; recovery.Save();
            checkpoint = "disable-owned-idle-timeout";
            await idlePolicy.DisableAfterOwnedStart(manager, owns);
            checkpoint = "data-host-session";
            await RunDataHost(prepared, groupId.Value, verifiedChannel, hostResult, cancellation);
            observed = (hostResult.StopRequested && !hostResult.Started) || (hostResult.ExitCode == 0 && hostResult.Stopped && hostResult.VirtualPadRemoved && hostResult.TransportThreadsJoined);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name; failureCheckpoint = ex is FiveGhzRequiredException ? "five-ghz-required" : checkpoint;
            try { failureHotspotState = manager.TetheringOperationalState.ToString(); } catch { }
            var bandFailure = ex as FiveGhzRequiredException;
            Log("test-error", new { type = errorType, hresult = $"0x{ex.HResult:X8}", checkpoint = failureCheckpoint, hotspot_state = failureHotspotState,
                band_failure_reason = bandFailure?.ReasonCode, observed_channel = bandFailure?.Channel, active_groups = bandFailure?.GroupCount, start_status = startStatus });
        }
        finally
        {
            // Cancellation of the observation never cancels necessary cleanup.
            if (owns && (!hostResult.Started || hostResult.Stopped))
            {
                try
                {
                    Log("stopping-owned-hotspot", new { start_result = startStatus });
                    var operation = manager.StopTetheringAsync();
                    var stopTask = operation.AsTask();
                    try
                    {
                        var stop = await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
                        stopStatus = stop.Status.ToString();
                        stopped = stop.Status == TetheringOperationStatus.Success;
                    }
                    catch
                    {
                        var late = await CancelAndSettle(() => operation.Cancel(), stopTask, TimeSpan.FromSeconds(2));
                        stopUnsettled = late.Pending;
                        if (late.HasResult) { stopStatus = late.Result!.Status.ToString(); stopped = late.Result.Status == TetheringOperationStatus.Success; }
                        Log("stop-cancel-settlement", new { pending = late.Pending, cancel_call_failed = late.CancelCallFailed, stop_status = stopStatus });
                        throw;
                    }
                }
                catch (Exception ex) { Log("stop-failed", new { type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" }); }
            }
            else if (owns) Log("child-still-running-hotspot-not-stopped", new { recovery_required = true });
            else if (startUnsettled) Log("start-still-pending", new { cleanup_proven = false, manual_recovery_may_be_required = true });
            // Restore even when observation or AP cleanup failed. No contradictory
            // setter is issued while an earlier idle-policy operation is pending.
            await idlePolicy.Restore();
            try
            {
                off = EmptyHotspot(manager.TetheringOperationalState, manager.ClientCount) && ChannelInspection.ActiveGroups().Length == 0;
                ethernetOk = UplinkState() == prepared.UplinkAddressState;
                if (off) persistentSame = prepared.Original.Same(manager.GetCurrentAccessPointConfiguration());
                startupSame = prepared.StartupModes.SequenceEqual(StartupModes());
            }
            catch (Exception ex) { Log("restoration-check-failed", new { type = ex.GetType().Name }); }
            bool resourcesClean = off && idlePolicy.Restored && !idlePolicy.Pending && !startUnsettled && !stopUnsettled && (!hostResult.Started || hostResult.Stopped);
            if (resourcesClean && recovery is not null)
            {
                try { File.Delete(PrivateFiles.Recovery); recovery = null; }
                catch (Exception ex) { resourcesClean = false; Log("recovery-marker-clear-failed", new { type = ex.GetType().Name }); }
            }
            bool ok = started && observed && stopped && off && idlePolicy.Restored && ethernetOk && persistentSame && startupSame && errorType is null && logFailures == 0 && resourcesClean;
            var receipt = new { ok, started, start_status = startStatus, start_unsettled = startUnsettled, own_start_success = owns,
                channel, data_host_completed = hostResult.Started && observed, data_host = hostResult, wireless_pad_reports_observed = hostResult.PadReports > 0, physical_wireless_input_verified = false, physical_fox_movement_verified = false,
                stop_requested = hostResult.StopRequested, request_stop_file = "request-stop", automatic_extension = false,
                stop_status = stopStatus, stop_success = stopped, stop_unsettled = stopUnsettled, hotspot_off = off,
                recovery_required = !resourcesClean, lifetime = "launcher-or-explicit-stop",
                original_no_connections_timeout_enabled = idlePolicy.Original, idle_timeout_disable_attempted = idlePolicy.DisableAttempted,
                idle_timeout_disabled_observed = idlePolicy.DisabledObserved, idle_timeout_restore_attempted = idlePolicy.RestoreAttempted,
                final_no_connections_timeout_enabled = idlePolicy.FinalEnabled, idle_timeout_policy_restored = idlePolicy.Restored, idle_timeout_operation_pending = idlePolicy.Pending,
                uplink_up_internet_and_addresses_unchanged = ethernetOk, persistent_hotspot_configuration_unchanged = persistentSame,
                service_startup_modes_unchanged = startupSame, original_band = prepared.Original.Band.ToString(),
                configuration_setter_called = false, direct_ics_or_service_setters_called = false,
                ics_runtime_side_effects_possible = true, ics_full_configuration_restoration_not_independently_proven = true,
                elapsed_seconds = watch.Elapsed.TotalSeconds, error_type = errorType, failure_checkpoint = failureCheckpoint, failure_hotspot_state = failureHotspotState, log_failures = logFailures, status_mirror_failures = progressMirrorFailures,
                warning = "The public API has no session lease and cannot guarantee shutdown within the requested time if Windows never completes Start/Stop. No unowned hotspot is stopped." };
            Log("stopped", receipt);
            lock (logGate)
            {
                loggingClosed = true;
                receipt = receipt with { ok = receipt.ok && logFailures == 0, log_failures = logFailures, status_mirror_failures = progressMirrorFailures };
                try { PrivateFiles.WriteAtomic(Path.Combine(output!, "receipt.json"), receipt); finalOutcome = receipt.ok; }
                catch { logFailures++; }
            }
        }
        return finalOutcome && logFailures == 0 ? 0 : 1;
    }

    private static async Task<int> Main(string[] args)
    {
        string mode = "plan", configPath = PrivateFiles.Config, directory = "", targetHash = "", importPath = "";
        Guid setupWifi = Guid.Empty, setupUplink = Guid.Empty;
        int parentPid = 0; ulong parentCreation = 0;
        var seen = new HashSet<string>();
        try
        {
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !seen.Add(args[i])) throw new ArgumentException("Duplicate or incomplete argument.");
                switch (args[i])
                {
                    case "--mode": mode = args[i + 1]; break;
                    case "--config": configPath = args[i + 1]; break;
                    case "--session": directory = args[i + 1]; break;
                    case "--wifi-guid": if (!Guid.TryParse(args[i + 1], out setupWifi)) throw new ArgumentException(); break;
                    case "--uplink-guid": if (!Guid.TryParse(args[i + 1], out setupUplink)) throw new ArgumentException(); break;
                    case "--target-id-hash": targetHash = args[i + 1]; break;
                    case "--import": importPath = args[i + 1]; break;
                    case "--parent-pid": if (!int.TryParse(args[i + 1], out parentPid)) throw new ArgumentException(); break;
                    case "--parent-creation-filetime": if (!ulong.TryParse(args[i + 1], out parentCreation)) throw new ArgumentException(); break;
                    default: throw new ArgumentException("Unknown option.");
                }
            }
            if (mode is not ("plan" or "installation-check" or "capabilities" or "discover" or "pair" or "import-pairing" or "forget" or "validate" or "run" or "recover" or "selftest")) throw new ArgumentException("Unknown mode.");
            if (mode is not ("discover" or "pair" or "import-pairing") && (setupWifi != Guid.Empty || setupUplink != Guid.Empty || targetHash.Length != 0)) throw new ArgumentException("Adapter flags only apply to setup/import.");
            if (mode != "import-pairing" && importPath.Length != 0) throw new ArgumentException("Import path only applies to Import Pairing.");
            if (mode == "plan") { Log("plan", new { activation = false, modes = new[] { "capabilities", "discover", "pair", "import-pairing", "forget", "validate", "run", "recover", "selftest" }, lifetime = "Explicit Start until launcher exits or Stop is requested.", band_preference = "5ghz-only" }); return 0; }
            if (mode == "selftest") return await SelfTest();
            if (mode == "capabilities") return await Capabilities();
            if (mode == "installation-check") return await Capabilities(installationOnly: true);
            if (!PrivateFiles.SamePath(configPath, PrivateFiles.Config)) throw new ArgumentException("Use the installed current configuration.");
            package = PackageManifest.Load();
            using var ownerLock = RecoveryState.AcquireOwnerLock();
            if (mode == "forget")
            {
                if (directory.Length != 0 || parentPid != 0 || parentCreation != 0) throw new ArgumentException("Session flags only apply to Run.");
                return Forget();
            }
            if (mode == "recover" && File.Exists(SetupActive)) return await RecoverSetup();
            if (File.Exists(SetupActive)) { checkpoint = "setup-recovery-required"; throw new InvalidOperationException("Unresolved setup state. Use explicit Recover."); }
            if (mode == "import-pairing")
            {
                if (importPath.Length == 0 || setupWifi == Guid.Empty || setupUplink == Guid.Empty || targetHash.Length != 0 || directory.Length != 0 || parentPid != 0 || parentCreation != 0)
                    throw new ArgumentException("Import needs a private export and local adapter identifiers only.");
                return ImportPairing(importPath, setupWifi, setupUplink);
            }
            if (mode is "discover" or "pair")
            {
                if (File.Exists(PrivateFiles.Recovery)) throw new InvalidOperationException("Recover the previous controller session first.");
                if (directory.Length != 0 || parentPid != 0 || parentCreation != 0 || (mode == "discover" && targetHash.Length != 0)) throw new ArgumentException("Invalid setup flags.");
                return await Setup(mode, setupWifi, setupUplink, targetHash);
            }
            config = Configuration.Load(configPath);
            if (mode == "recover") return await Recover();
            if (!ProvisionMatches(config)) { checkpoint = "pairing-required"; throw new InvalidOperationException("Complete guided pairing before Start."); }
            if (File.Exists(PrivateFiles.Recovery)) throw new InvalidOperationException("Unresolved controller recovery marker. Use explicit Recover.");
            if (mode == "run")
            {
                string parent = Path.Combine(Root, "sessions");
                string full = Path.GetFullPath(directory);
                if (directory.Length == 0 || !PrivateFiles.SamePath(Path.GetDirectoryName(full)!, parent)) throw new ArgumentException("Session must be directly under installed sessions.");
                sessionId = Path.GetFileName(full);
                if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit)) throw new ArgumentException("Session ID must be32hex.");
                PrivateFiles.CreateDirectory(parent);
                if (Directory.Exists(full)) PrivateFiles.RequirePrivate(full, true); else PrivateFiles.CreateDirectory(full);
                if (new[] { "owner.json", "status.json", "receipt.json", "data-host" }.Any(n => File.Exists(Path.Combine(full, n)) || Directory.Exists(Path.Combine(full, n))))
                    throw new InvalidOperationException("Session already contains owner state.");
                output = full;
                launcher = new LauncherLifetime(parentPid, parentCreation);
            }
            else if (directory.Length != 0 || parentPid != 0 || parentCreation != 0) throw new ArgumentException("Session flags only apply to Run.");
            var prepared = await Prepare().WaitAsync(TimeSpan.FromSeconds(30));
            if (mode == "validate") return 0;
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try { return await Run(prepared, cancel.Token); }
            finally { Console.CancelKeyPress -= handler; launcher?.Dispose(); }
        }
        catch (Exception ex)
        {
            if (ex is FiveGhzRequiredException) checkpoint = "five-ghz-required";
            var bandFailure = ex as FiveGhzRequiredException;
            Log("failed", new { type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}", checkpoint,
                band_failure_reason = bandFailure?.ReasonCode, observed_channel = bandFailure?.Channel, active_groups = bandFailure?.GroupCount,
                message = FailureHelp(checkpoint), recovery_required = File.Exists(PrivateFiles.Recovery) || File.Exists(SetupActive) });
            launcher?.Dispose(); return 2;
        }
    }
    private static string FailureHelp(string stage) => stage switch {
        "five-ghz-required" => ConnectionSettings.FiveGhzOnlyMessage,
        "pairing-network-unavailable" => "Windows could not verify the temporary pairing network. Pairing may use 2.4 GHz or 5 GHz; controller input still requires 5 GHz.",
        "setup-interface-changed" => "The Wi-Fi or Internet connection changed. Restore the selected adapters, then check setup again.",
        "setup-single-radio" => "First pairing requires exactly one enabled Wi-Fi adapter. Select that adapter and an Internet connection.",
        "selected-target" => "Discover and select the controller again. Existing pending credentials are retained; a different controller requires explicit setup replacement.",
        "pairing-bootstrap-channel" => ConnectionSettings.FiveGhzOnlyMessage,
        "setup-recovery-required" => "A previous setup did not prove cleanup. Use Recover; if a hotspot remains active, turn it off in Windows first.",
        "pairing-required" => "Complete guided pairing before starting the controller.",
        "forget-controller" => "Stop the controller and Windows hotspot, resolve any pending recovery, and keep a network connected before forgetting saved pairing.",
        "read-only-preflight" => "Check Windows 11 24H2, Wi-Fi/Location access, an active Internet connection, installed drivers, and that Mobile Hotspot is off.",
        "verify-channel" => ConnectionSettings.FiveGhzOnlyMessage,
        "argument-validation" => "Check the installed package and command arguments.",
        _ => "The controller operation stopped at the reported checkpoint. Check the local session receipt before retrying."
    };
}
