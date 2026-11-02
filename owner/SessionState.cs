using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class LauncherLifetime : IDisposable
{
    private readonly Process process;
    internal LauncherLifetime(int pid, ulong expectedCreation)
    {
        if (pid <= 0 || pid == Environment.ProcessId || expectedCreation == 0) throw new ArgumentException("A live launcher identity is required.");
        process = Process.GetProcessById(pid);
        if (Creation(process) != expectedCreation || process.HasExited) { process.Dispose(); throw new InvalidOperationException("Launcher identity changed."); }
    }
    internal bool Alive { get { try { return !process.HasExited; } catch { return false; } } }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    internal static ulong Creation(Process process)
    {
        if (!GetProcessTimes(process.Handle, out ulong creation, out _, out _, out _) || creation == 0) throw new InvalidOperationException("Process creation identity unavailable.");
        return creation;
    }
    internal static bool MatchesLive(int pid, ulong creation)
    {
        try { using var candidate = Process.GetProcessById(pid); return !candidate.HasExited && Creation(candidate) == creation; }
        catch (ArgumentException) { return false; }
    }
    public void Dispose() => process.Dispose();
}

internal sealed class RecoveryState
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("config_sha256")] public string ConfigHash { get; init; } = "";
    [JsonPropertyName("owner_pid")] public int OwnerPid { get; init; }
    [JsonPropertyName("owner_creation_filetime")] public ulong OwnerCreation { get; init; }
    [JsonPropertyName("original_idle_timeout_enabled")] public bool OriginalIdleTimeout { get; init; }
    [JsonPropertyName("owned_start_success")] public bool OwnedStart { get; set; }
    [JsonPropertyName("go_guid")] public Guid? GroupId { get; set; }
    [JsonPropertyName("channel")] public int Channel { get; set; }
    [JsonPropertyName("go_mac")] public int[]? GroupMac { get; set; }
    [JsonPropertyName("idle_disable_planned")] public bool IdleDisablePlanned { get; set; }
    [JsonPropertyName("child_pid")] public int? ChildPid { get; set; }
    [JsonPropertyName("child_creation_filetime")] public ulong? ChildCreation { get; set; }
    internal void Save() => PrivateFiles.WriteAtomic(PrivateFiles.Recovery, this);
    internal static RecoveryState Load(Configuration config) => Parse(PrivateFiles.Read(PrivateFiles.Recovery), config);
    internal static RecoveryState Parse(byte[] bytes, Configuration config)
    {
        using (var document = JsonDocument.Parse(bytes))
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new FormatException("Recovery state shape is invalid.");
        var state = JsonSerializer.Deserialize<RecoveryState>(bytes, Configuration.StrictJson) ?? throw new FormatException("Recovery state empty.");
        if (state.Schema != 1 || state.SessionId is null || state.ConfigHash is null || state.SessionId.Length != 32 || !state.SessionId.All(Uri.IsHexDigit) || state.OwnerPid <= 0 || state.OwnerCreation == 0 ||
            !state.ConfigHash.Equals(config.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            state.GroupId.HasValue != (state.GroupMac is not null) ||
            (state.GroupId.HasValue && (!ConnectionSettings.ValidChannel(state.Channel) || state.GroupId == Guid.Empty || state.GroupMac!.Length != 6 || state.GroupMac.Any(b => b is < 0 or > 255) || state.GroupMac.All(b => b == 0) || (state.GroupMac[0] & 1) != 0)) ||
            (!state.OwnedStart && state.GroupId.HasValue) ||
            (state.IdleDisablePlanned && (!state.OriginalIdleTimeout || !state.OwnedStart || !state.GroupId.HasValue)) ||
            state.ChildPid.HasValue != state.ChildCreation.HasValue ||
            (state.ChildPid.HasValue && (state.ChildPid <= 0 || state.ChildCreation == 0 || !state.OwnedStart || !state.GroupId.HasValue)))
            throw new FormatException("Recovery state does not match the current configuration.");
        return state;
    }
    internal static FileStream AcquireOwnerLock()
    {
        PrivateFiles.RequirePrivate(PrivateFiles.Root, true);
        string path = Path.Combine(PrivateFiles.Root, "owner.lock");
        if (File.Exists(path)) PrivateFiles.RequirePrivate(path, false);
        // Unlike a named Mutex this lock can safely span async continuations.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
