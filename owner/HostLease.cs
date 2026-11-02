using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    private sealed class HostResult
    {
        public bool Started { get; set; }
        public bool TransportReady { get; set; }
        public bool Stopped { get; set; }
        public int? ExitCode { get; set; }
        public bool Killed { get; set; }
        public bool VirtualPadRemoved { get; set; }
        public bool TransportThreadsJoined { get; set; }
        public ulong PadReports { get; set; }
        public bool ExpectedControllerSession { get; set; }
        public int? XinputSlot { get; set; }
        public string? FirmwareVersion { get; set; }
        public int Heartbeats { get; set; }
        public bool ExpectedControllerListedClient { get; set; }
        public uint MaximumListedClients { get; set; }
        public bool StopRequested { get; set; }
        public string? LastCheckpoint { get; set; }
        public string? LastHotspotState { get; set; }
        public string? ChildFailureStage { get; set; }
    }
    private static string OwnerExecutable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Process image unavailable.");
        if (!Path.GetFullPath(exe).Equals(Path.Combine(Root, @"bin\owner\ShieldControllerOwner.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the exact reviewed owner apphost executable.");
        return exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    private static ulong OwnerCreationFiletime()
    {
        using var process = Process.GetCurrentProcess();
        if (!GetProcessTimes(process.Handle, out ulong creation, out _, out _, out _) || creation == 0)
            throw new InvalidOperationException("Owner creation FILETIME unavailable.");
        return creation;
    }
    private static byte[] GroupMac(Guid id)
    {
        if (!ChannelInspection.IsGroupOwner(id)) throw new InvalidOperationException("Selected interface is not a Wi-Fi Direct group owner.");
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => Guid.TryParse(n.Id, out var parsed) && parsed == id);
        var mac = nic.GetPhysicalAddress().GetAddressBytes();
        if (nic.OperationalStatus != OperationalStatus.Up || mac.Length != 6 || mac.All(b => b == 0) || (mac[0] & 1) != 0)
            throw new InvalidOperationException("GO MAC unavailable or invalid.");
        return mac;
    }
    private static string WritePrivateContext(Guid groupId, byte[] mac, int channel)
    {
        string context = output!;
        PrivateFiles.RequirePrivate(context, true);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var receipt = new {
            schema = 1, owner_pid = Environment.ProcessId, owner_creation_filetime = OwnerCreationFiletime(),
            owner_executable_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(OwnerExecutable()))),
            adapter_guid = groupId.ToString("B"), adapter_mac = mac.Select(b => (int)b).ToArray(), channel,
            created_unix_ms = now, session_id = sessionId, controller_mac = config.ControllerMac, config_sha256 = config.SourceSha256,
        };
        string path = Path.Combine(context, "owner.json");
        Log("owner-context-file-write", new { });
        PrivateFiles.WriteAtomic(path, receipt);
        return path;
    }
    private static string ExpectedMac() => Convert.ToHexString(config.ControllerMac.Select(b => (byte)b).ToArray());
    private static string NormalizeMac(string value)
    {
        string normalized = value.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (normalized.Length != 12 || normalized.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Invalid MAC metadata.");
        return normalized;
    }
    private sealed record Association(uint Clients, int? ListedClients, bool? Expected, string? ErrorType, int? Hresult);
    internal static bool ExpectedHostCompletion(bool stopRequested, int? exitCode, bool stopped, bool padRemoved, bool threadsJoined) =>
        stopRequested && exitCode == 0 && stopped && padRemoved && threadsJoined;
    private static Association ReadAssociation(NetworkOperatorTetheringManager manager, string expected)
    {
        uint count = manager.ClientCount;
        try
        {
            var clients = manager.GetTetheringClients();
            bool match = clients.Any(c => NormalizeMac(c.MacAddress) == expected);
            return new(count, clients.Count, match, null, null);
        }
        catch (Exception error) { return new(count, null, null, error.GetType().Name, error.HResult); }
    }
    private static async Task RunDataHost(Prepared prepared, Guid groupId, int channel, HostResult result, CancellationToken cancellation)
    {
        checkpoint = result.LastCheckpoint = "data-host-preflight";
        if (ShouldStop)
        {
            result.StopRequested = true;
            Log("stop-requested-before-child", new { request_file = "request-stop", child_started = false });
            return;
        }
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ChildPath))) != ChildHash) throw new InvalidOperationException("Data host changed before launch.");
        byte[] mac = GroupMac(groupId);
        string expectedMac = ExpectedMac();
        checkpoint = result.LastCheckpoint = "private-owner-context";
        string context = WritePrivateContext(groupId, mac, channel);
        string childOutput = Path.Combine(output!, "data-host"); // Child requires this new directory's parent to exist.
        var start = new ProcessStartInfo(ChildPath) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(ChildPath)!,
        };
        foreach (string arg in new[] { "--run", "--owner-receipt", context, "--output", childOutput }) start.ArgumentList.Add(arg);
        Log("data-host-launching", new { continuous_while_owner_and_launcher_alive = true, exact_group_verified = true, private_owner_context = true,
            heartbeat_interval_ms = OwnerHeartbeatLease.IntervalMilliseconds, validation_lease_ms = OwnerHeartbeatLease.ValidationMilliseconds, configuration_pin_verified = true }, required: true);
        checkpoint = result.LastCheckpoint = "data-host-launch";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Data host failed to start.");
        result.Started = true;
        Task stdout = Task.CompletedTask; Task<string> stderr = Task.FromResult("");
        var lifetime = Stopwatch.StartNew();
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var heartbeatLease = new OwnerHeartbeatLease(lifetime.ElapsedMilliseconds);
        // Start immediately; even the recovery write must not consume the child's
        // three-second initial heartbeat budget.
        Task heartbeat = Task.Run(() => KeepChildAlive(child, result, heartbeatLease, lifetime, stopHeartbeat));
        Task<Association>? association = null; double nextAssociation = 0;
        Association? previousAssociation = null;
        try
        {
            stdout = PumpChild(child.StandardOutput, result); stderr = child.StandardError.ReadToEndAsync();
            if (recovery is null) throw new InvalidOperationException("Recovery state missing.");
            recovery.ChildPid = child.Id; recovery.ChildCreation = LauncherLifetime.Creation(child); recovery.Save();
            while (!child.HasExited)
            {
                cancellation.ThrowIfCancellationRequested();
                if (heartbeat.IsCompleted) await heartbeat;
                if (stopHeartbeat.IsCancellationRequested) break;
                if (stdout.IsFaulted) await stdout;
                if (ShouldStop)
                {
                    result.StopRequested = true;
                    Log("stop-requested", new { request_file = "request-stop", launcher_alive = launcher?.Alive == true, close_heartbeat_then_await_child = true });
                    break;
                }
                if (association?.IsCompleted == true)
                {
                    try
                    {
                        var sample = await association;
                        result.MaximumListedClients = Math.Max(result.MaximumListedClients, sample.Clients);
                        result.ExpectedControllerListedClient |= sample.Expected == true;
                        if (sample != previousAssociation)
                            Log("listed-clients-changed", new { client_count = sample.Clients, listed_client_count = sample.ListedClients, expected_controller_mac_equal = sample.Expected, error_type = sample.ErrorType, hresult = sample.Hresult });
                        previousAssociation = sample;
                    }
                    catch (Exception error) { Log("association-read-error", new { type = error.GetType().Name, hresult = error.HResult }); }
                    association = null;
                }
                // One read-only metadata query at a time; a stalled enumeration does
                // not block heartbeat or spawn an unbounded stream of pending tasks.
                if (association == null && lifetime.Elapsed.TotalSeconds >= nextAssociation)
                {
                    association = Task.Run(() => ReadAssociation(prepared.Manager, expectedMac));
                    nextAssociation = lifetime.Elapsed.TotalSeconds + 5;
                }
                checkpoint = result.LastCheckpoint = "verify-owned-network";
                heartbeatLease.BeginValidation(lifetime.ElapsedMilliseconds);
                // Only one complete validator is in flight. A stalled native call
                // cannot renew the lease or cause a stream of replacement tasks.
                Task<string> validation = Task.Run(() => {
                    try
                    {
                        string state = ValidateOwnedHost(prepared, groupId, mac, channel, stopHeartbeat.Token);
                        heartbeatLease.CompleteValidation(lifetime.ElapsedMilliseconds);
                        return state;
                    }
                    catch { heartbeatLease.Revoke(); throw; }
                });
                // Native queries cannot always be cancelled. Observe a late fault
                // without allowing the completed task to renew a revoked lease.
                _ = validation.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try { result.LastHotspotState = await validation.WaitAsync(TimeSpan.FromMilliseconds(OwnerHeartbeatLease.ValidationMilliseconds), stopHeartbeat.Token); }
                catch (HostValidationException error) { checkpoint = result.LastCheckpoint = error.Stage; throw; }
                await Task.Delay(OwnerHeartbeatLease.IntervalMilliseconds, stopHeartbeat.Token);
            }
        }
        catch (OperationCanceledException) when (result.StopRequested || cancellation.IsCancellationRequested)
        { result.StopRequested = true; }
        catch (OperationCanceledException)
        {
            checkpoint = result.LastCheckpoint = "owner-validation-lease";
            if (heartbeat.IsCompleted) await heartbeat;
            throw new InvalidOperationException("Data host heartbeat ended without a requested stop.");
        }
        finally
        {
            heartbeatLease.Revoke(); stopHeartbeat.Cancel();
            try { await heartbeat.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { Log("owner-heartbeat-stopped", new { type = error.GetType().Name, validation_lease_ms = OwnerHeartbeatLease.ValidationMilliseconds }); }
            // Close heartbeat and stop/await only this owned child before its AP stops.
            try { child.StandardInput.Close(); } catch { }
            try
            {
                if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                try { child.Kill(true); result.Killed = true; await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) { Log("data-host-kill-failed", new { type = error.GetType().Name }); }
            }
            result.Stopped = child.HasExited;
            if (result.Stopped) result.ExitCode = child.ExitCode;
            try
            {
                await stdout.WaitAsync(TimeSpan.FromSeconds(2));
                string errors = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
                Log("data-host-stopped", new { result, stderr_bytes = errors.Length, physical_fox_movement_verified = false });
            }
            catch (Exception error) { logFailures++; Log("data-host-evidence-error", new { type = error.GetType().Name }); }
        }
        checkpoint = result.LastCheckpoint = "verify-child-completion";
        if (!result.StopRequested)
        {
            checkpoint = result.LastCheckpoint = "data-host-unexpected-exit";
            throw new InvalidOperationException("Data host exited without an explicit stop or launcher exit.");
        }
        if (!ExpectedHostCompletion(result.StopRequested, result.ExitCode, result.Stopped, result.VirtualPadRemoved, result.TransportThreadsJoined))
            throw new InvalidOperationException("Data host did not complete and release normally.");
    }
    private static async Task PumpChild(StreamReader reader, HostResult result)
    {
        while (await reader.ReadLineAsync() is string line)
        {
            if (line.Length > 16384) throw new InvalidDataException("Oversized child metadata row.");
            using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
            string? stage = row.GetProperty("stage").GetString(); var detail = row.GetProperty("detail");
            BoundedHistory.Append(Path.Combine(output!, "data-host-stdout.jsonl"), line);
            if (stage == "transport-ready") { result.TransportReady = true; Log("transport-ready", new { gamepad_only = true }); }
            if (stage == "data-host-released") { result.VirtualPadRemoved = detail.GetProperty("virtual_pad_removed").GetBoolean(); result.TransportThreadsJoined = detail.GetProperty("transport_threads_joined").GetBoolean(); result.TransportReady = false; }
            string? readiness = ApplyReadiness(stage, stage == "xinput-ready" ? detail.GetProperty("slot").GetInt32() : null, result);
            if (stage == "controller-firmware" && ApplyFirmware(detail, result)) Log("controller-firmware", new { });
            if (readiness is not null) Log(readiness, new { xinput_slot = result.XinputSlot, reason = stage });
            if (detail.TryGetProperty("pad_reports", out var reports)) result.PadReports = Math.Max(result.PadReports, reports.GetUInt64());
            if (stage == "finished" && detail.TryGetProperty("error_code", out var code) && code.ValueKind != JsonValueKind.Null)
            {
                string value = code.GetString() ?? throw new InvalidDataException("Missing redacted child error code.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[a-z_]{1,64}\\z")) throw new InvalidDataException("Unexpected child error code.");
                result.ChildFailureStage = value;
            }
        }
    }
    private static bool ApplyFirmware(JsonElement detail, HostResult result)
    {
        if (!result.ExpectedControllerSession || !detail.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.String || !ControllerFirmware.IsValid(version.GetString())) return false;
        result.FirmwareVersion = version.GetString(); return true;
    }
    private static string? ApplyReadiness(string? stage, int? slot, HostResult result)
    {
        if (stage == "expected-controller-resumed")
        {
            bool wasDetected = result.ExpectedControllerSession;
            result.ExpectedControllerSession = true;
            return wasDetected ? null : "controller-detected";
        }
        if (stage == "xinput-ready")
        {
            if (slot is null or < 0 or > 3) throw new InvalidDataException("Invalid XInput readiness slot.");
            result.XinputSlot = slot; return "connected";
        }
        if (stage is "expected-controller-session" or "stale-input-released" or "controller-session-released" or "data-host-released")
        {
            bool wasConnected = result.XinputSlot is not null; result.XinputSlot = null;
            bool wasDetected = result.ExpectedControllerSession;
            if (stage != "stale-input-released") result.FirmwareVersion = null;
            if (stage == "expected-controller-session") result.ExpectedControllerSession = true;
            if (stage is "controller-session-released" or "data-host-released") result.ExpectedControllerSession = false;
            // Report session arrival/loss even before the first gamepad input report.
            // Receiver readiness by itself does not mean the controller was detected.
            return wasConnected ? "disconnected"
                : stage == "expected-controller-session" ? "controller-detected"
                : wasDetected && !result.ExpectedControllerSession ? "controller-lost" : null;
        }
        return null;
    }

}
