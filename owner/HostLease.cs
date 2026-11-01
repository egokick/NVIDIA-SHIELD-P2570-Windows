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
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => Guid.TryParse(n.Id, out var parsed) && parsed == id && ChannelInspection.IsBaseGroup(n.Description));
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
        Log("data-host-launching", new { continuous_while_owner_and_launcher_alive = true, exact_group_verified = true, private_owner_context = true, heartbeat_interval_ms = 1000, configuration_pin_verified = true }, required: true);
        checkpoint = result.LastCheckpoint = "data-host-launch";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Data host failed to start.");
        result.Started = true;
        Task stdout = Task.CompletedTask; Task<string> stderr = Task.FromResult("");
        var lifetime = Stopwatch.StartNew();
        Task<Association>? association = null; double nextAssociation = 0;
        Association? previousAssociation = null;
        try
        {
            stdout = PumpChild(child.StandardOutput, result); stderr = child.StandardError.ReadToEndAsync();
            if (recovery is null) throw new InvalidOperationException("Recovery state missing.");
            recovery.ChildPid = child.Id; recovery.ChildCreation = LauncherLifetime.Creation(child); recovery.Save();
            while (!child.HasExited)
            {
                cancellation.ThrowIfCancellationRequested(); var beat = Stopwatch.StartNew();
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
                checkpoint = result.LastCheckpoint = "verify-hotspot-on";
                var state = prepared.Manager.TetheringOperationalState;
                result.LastHotspotState = state.ToString();
                if (state != TetheringOperationalState.On) throw new InvalidOperationException("Owned hotspot no longer On.");
                checkpoint = result.LastCheckpoint = "verify-active-go";
                if (!ChannelInspection.ActiveGroups().SequenceEqual(new[] { groupId })) throw new InvalidOperationException("Owned GO changed.");
                checkpoint = result.LastCheckpoint = "verify-go-mac";
                if (!GroupMac(groupId).SequenceEqual(mac)) throw new InvalidOperationException("Owned GO MAC changed.");
                checkpoint = result.LastCheckpoint = "verify-uplink";
                if (UplinkState() != prepared.UplinkAddressState) throw new InvalidOperationException("Uplink changed.");
                checkpoint = result.LastCheckpoint = "verify-go-channel";
                int currentChannel = await Task.Run(() => ChannelInspection.Read(groupId)).WaitAsync(TimeSpan.FromMilliseconds(700), cancellation);
                if (currentChannel != channel) throw new InvalidOperationException("Owned hotspot channel changed.");
                try
                {
                    checkpoint = result.LastCheckpoint = "send-child-heartbeat";
                    await child.StandardInput.WriteAsync("alive\n").WaitAsync(TimeSpan.FromMilliseconds(500), cancellation);
                    await child.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(500), cancellation);
                    result.Heartbeats++;
                }
                catch (IOException) when (child.HasExited) { break; }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 1000 - beat.Elapsed.TotalMilliseconds)), cancellation);
            }
        }
        finally
        {
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
        if (result.ExitCode != 0 || !result.Stopped || !result.VirtualPadRemoved || !result.TransportThreadsJoined)
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
            if (stage == "data-host-released") { result.VirtualPadRemoved = detail.GetProperty("virtual_pad_removed").GetBoolean(); result.TransportThreadsJoined = detail.GetProperty("transport_threads_joined").GetBoolean(); result.XinputSlot = null; }
            string? readiness = ApplyReadiness(stage, stage == "xinput-ready" ? detail.GetProperty("slot").GetInt32() : null, result);
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
    private static string? ApplyReadiness(string? stage, int? slot, HostResult result)
    {
        if (stage == "xinput-ready")
        {
            if (slot is null or < 0 or > 3) throw new InvalidDataException("Invalid XInput readiness slot.");
            result.XinputSlot = slot; return "connected";
        }
        if (stage is "expected-controller-session" or "stale-input-released" or "controller-session-released")
        {
            bool wasConnected = result.XinputSlot is not null; result.XinputSlot = null;
            if (stage == "expected-controller-session") result.ExpectedControllerSession = true;
            return wasConnected ? "disconnected" : null;
        }
        return null;
    }

}
