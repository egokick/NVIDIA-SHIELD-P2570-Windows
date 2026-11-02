using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ShieldController;

internal sealed class OwnerCommandException(string message, FiveGhzFailureText? bandFailure = null) : InvalidOperationException(message)
{
    internal FiveGhzFailureText? BandFailure { get; } = bandFailure;
}

internal sealed class OwnerClient : IDisposable, IPairingService
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA-SHIELD-P2570-Windows");
    internal static string Executable => Path.Combine(Root, "bin", "owner", "ShieldControllerOwner.exe");
    private Process? session;
    private Task? sessionOutput;
    private string? sessionDirectory;
    private Process? commandProcess;
    internal bool Running => session is { HasExited: false };
    internal string? SessionFailureMessage { get; private set; }
    internal FiveGhzFailureText? SessionBandFailure { get; private set; }
    internal bool CommandPending => commandProcess is { HasExited: false };
    internal string LogDirectory => sessionDirectory ?? Root;
    internal event Action<JsonElement>? SessionEvent;
    internal event Action<int>? SessionEnded;
    internal event Action? SessionFailed;

    private static ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static Task ReadEvents(Process process, Action<JsonElement> accept, Action? failed = null) => ConsumeEvents(process.StandardOutput, accept, failed);
    internal static async Task ConsumeEvents(TextReader reader, Action<JsonElement> accept, Action? failed = null)
    {
        bool invalid = false;
        void Failed() { if (invalid) return; invalid = true; try { failed?.Invoke(); } catch { } }
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                // Keep draining after a malformed event so the owner can finish
                // its cleanup without blocking on a full redirected pipe.
                if (invalid || string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (line.Length > 65536) throw new InvalidDataException("Owner event exceeded its limit.");
                    using var json = JsonDocument.Parse(line);
                    accept(json.RootElement.Clone());
                }
                catch { Failed(); }
            }
        }
        catch { Failed(); throw; }
        if (invalid) throw new InvalidDataException("The controller service returned an invalid event.");
    }
    private static async Task DrainErrors(Process process)
    {
        // Native error strings can contain identifiers. They are never displayed or copied into tray logs.
        var buffer = new char[2048];
        while (await process.StandardError.ReadAsync(buffer) > 0) { }
    }
    internal async Task<IReadOnlyList<JsonElement>> Command(string[] args, TimeSpan timeout)
    {
        if (Running || CommandPending) throw new InvalidOperationException("Wait for the current connection or setup operation to finish.");
        commandProcess?.Dispose();
        var process = commandProcess = Process.Start(StartInfo(args)) ?? throw new IOException("The controller service could not start.");
        var events = new List<JsonElement>();
        var output = ReadEvents(process, item => { if (events.Count >= 2048) throw new InvalidDataException("Too many owner events."); events.Add(item); });
        var errors = DrainErrors(process);
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3));
            if (process.ExitCode != 0) throw new OwnerCommandException(ExplainFailure(events), ExplainBandFailure(events));
            return events;
        }
        catch (TimeoutException)
        {
            // Pairing/provider workers own bounded cleanup. Do not kill them during provisioning.
            throw new TimeoutException("Controller setup is taking longer than expected. Wait for cleanup before retrying; see the log folder.");
        }
    }
    internal static string ExplainFailure(IReadOnlyList<JsonElement> events)
    {
        if (ExplainBandFailure(events) is { } bandFailure) return bandFailure.Message;
        // Use only known checkpoints to select our own text. Native messages
        // and controller identifiers must never leak into the visible error.
        var failure = events.LastOrDefault(e => Phase(e) is "failed" or "setup-error");
        string? checkpoint = failure.ValueKind == JsonValueKind.Object && failure.TryGetProperty("detail", out var detail) ? OptionalString(detail, "checkpoint") : null;
        return checkpoint switch {
            "setup-interface-changed" => "The Wi-Fi or internet connection changed. Restore the selected adapters, then check setup again.",
            "pairing-network-unavailable" or "pairing-bootstrap-channel" => "Windows could not verify the temporary pairing network. Keep one Wi-Fi adapter enabled and try again. Pairing can use 2.4 or 5 GHz; controller input uses 5 GHz.",
            "setup-hotspot-start" => "Windows could not start the temporary pairing network. Check that Wi-Fi and your internet connection are ready, then try again.",
            "setup-hotspot-changed" => "The pairing network changed during setup. Wait for cleanup, then check setup again.",
            "setup-single-radio" => "Pairing needs one Wi-Fi adapter enabled. Turn off extra Wi-Fi adapters in Windows Settings, then try again.",
            "selected-target" => "The controller selection expired or differs from saved setup. Keep its logo flashing and search again. Open Settings to forget an old controller.",
            "read-only-preflight" => "Check Wi-Fi permission, your internet connection, and that Windows Mobile Hotspot is off. Open Settings for setup details.",
            "setup-recovery-required" => "The last setup needs cleanup. Open Settings and choose Recover connection.",
            _ => "Pairing didn't finish. Unplug USB, hold the green logo for three seconds, then try again. Open Help for logs."
        };
    }
    private static bool ReportedFiveGhzSupport(JsonElement value) => Phase(value) == "validated" && value.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object &&
        detail.TryGetProperty("supports_5ghz", out var supported) && supported.ValueKind == JsonValueKind.True;
    internal static FiveGhzFailureText? ExplainBandFailure(IReadOnlyList<JsonElement> events)
    {
        var failure = events.LastOrDefault(e => Phase(e) is "failed" or "setup-error");
        return ReadBandFailure(failure, events.Any(ReportedFiveGhzSupport), pairing: false);
    }
    private static FiveGhzFailureText? ReadBandFailure(JsonElement value, bool supportsFive, bool pairing)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object) return null;
        string? checkpoint = OptionalString(detail, "checkpoint");
        if (checkpoint != "five-ghz-required") return null;
        int? channel = detail.TryGetProperty("observed_channel", out var observed) && observed.ValueKind == JsonValueKind.Number && observed.TryGetInt32(out int number) ? number : null;
        return ConnectionSettings.DescribeFiveGhzFailure(OptionalString(detail, "band_failure_reason"), channel, supportsFive, pairing);
    }
    public async Task<Capabilities> GetCapabilities()
    {
        var events = await Command(["--mode", "capabilities"], TimeSpan.FromSeconds(40));
        var root = events.LastOrDefault(e => Phase(e) == "capabilities");
        if (root.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The controller service returned no capability report.");
        var d = root.GetProperty("detail");
        return new(d.GetProperty("paired").GetBoolean(), d.GetProperty("recovery_required").GetBoolean(),
            d.GetProperty("requirements").EnumerateArray().Select(r => new Requirement(r.GetProperty("id").GetString()!, r.GetProperty("label").GetString()!, r.GetProperty("available").GetBoolean())).ToArray(),
            ReadAdapters(d, "wifi_adapters"), ReadAdapters(d, "uplink_adapters"), OptionalString(d, "selected_wifi_guid"), OptionalString(d, "selected_uplink_guid"));
    }
    private static AdapterChoice[] ReadAdapters(JsonElement d, string key) => d.GetProperty(key).EnumerateArray().Select(a => new AdapterChoice(a.GetProperty("guid").GetString()!, a.GetProperty("name").GetString()!)).ToArray();
    internal static string Phase(JsonElement e) => e.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    internal static string? OptionalString(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public async Task<Candidate[]> Discover(string wifi, string uplink)
    {
        CreatePrivateDirectory(Root);
        var events = await Command(["--mode", "discover", "--wifi-guid", wifi, "--uplink-guid", uplink], PairingTiming.DiscoveryCommandBudget);
        return events.Where(e => Phase(e) == "candidate").Select(e => e.GetProperty("detail"))
            .Select(d => new Candidate(d.GetProperty("target_id_hash").GetString()!, d.GetProperty("display_name").GetString()!)).DistinctBy(c => c.TargetIdHash).ToArray();
    }
    public async Task Pair(Candidate target, string wifi, string uplink)
    {
        CreatePrivateDirectory(Root);
        var events = await Command(["--mode", "pair", "--target-id-hash", target.TargetIdHash, "--wifi-guid", wifi, "--uplink-guid", uplink], PairingTiming.PairCommandBudget);
        if (!events.Any(e => Phase(e) == "pairing-succeeded")) throw new InvalidDataException("No successful provisioning receipt was returned.");
    }
    internal async Task Recover() => _ = await Command(["--mode", "recover"], TimeSpan.FromSeconds(50));
    internal async Task Forget()
    {
        var events = await Command(["--mode", "forget"], TimeSpan.FromSeconds(20));
        if (!events.Any(e => Phase(e) == "pairing-forgotten")) throw new InvalidDataException("The saved pairing removal was not confirmed.");
    }
    internal void StartSession()
    {
        if (Running) return;
        SessionFailureMessage = null;
        SessionBandFailure = null;
        CreatePrivateDirectory(Root);
        string id = Guid.NewGuid().ToString("N");
        sessionDirectory = Path.Combine(Root, "sessions", id);
        CreatePrivateDirectory(Path.Combine(Root, "sessions"));
        CreatePrivateDirectory(sessionDirectory);
        using var me = Process.GetCurrentProcess();
        if (!GetProcessTimes(me.Handle, out ulong created, out _, out _, out _)) throw new InvalidOperationException("Launcher identity could not be read.");
        if (CommandPending) throw new InvalidOperationException("Wait for setup cleanup to finish.");
        session = Process.Start(StartInfo(["--mode", "run", "--session", sessionDirectory, "--parent-pid", Environment.ProcessId.ToString(), "--parent-creation-filetime", created.ToString()]))
            ?? throw new IOException("The controller service could not start.");
        Process own = session;
        sessionOutput = ObserveSession(own);
    }
    private async Task ObserveSession(Process own)
    {
        int code = -1;
        bool supportsFive = false;
        var outputFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Failed()
        {
            try { File.WriteAllText(Path.Combine(sessionDirectory!, "request-stop"), ""); } catch { }
            outputFailure.TrySetResult();
            SessionFailed?.Invoke();
        }
        try
        {
            Task output = ReadEvents(own, e => {
                supportsFive |= ReportedFiveGhzSupport(e);
                if (Phase(e) is "failed" or "test-error" && ReadBandFailure(e, supportsFive, pairing: false) is { } bandFailure)
                {
                    SessionBandFailure = bandFailure;
                    SessionFailureMessage = bandFailure.Message;
                }
                SessionEvent?.Invoke(e);
            }, Failed);
            Task errors = DrainErrors(own);
            Task exit = own.WaitForExitAsync();
            if (await Task.WhenAny(exit, outputFailure.Task) == outputFailure.Task)
                await exit.WaitAsync(TimeSpan.FromSeconds(35));
            else await exit;
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3));
            code = own.ExitCode;
        }
        catch { /* Report failure without exposing native error text. */ }
        finally { SessionEnded?.Invoke(code); }
    }
    internal async Task StopSession()
    {
        if (CommandPending) await commandProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        if (session is null) return;
        if (!session.HasExited)
        {
            await File.WriteAllTextAsync(Path.Combine(sessionDirectory!, "request-stop"), "");
            await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
        }
        if (sessionOutput is not null) await sessionOutput.WaitAsync(TimeSpan.FromSeconds(3));
        session.Dispose(); session = null; sessionOutput = null;
    }
    internal static void CreatePrivateDirectory(string path)
    {
        var d = Directory.CreateDirectory(path);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows account identity unavailable.");
        var original = d.GetAccessControl();
        if (!sid.Equals(original.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Application data belongs to another account.");
        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Application data cannot be a link.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        d.SetAccessControl(acl);
    }
    public void Dispose() { session?.Dispose(); commandProcess?.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint handle, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
}
