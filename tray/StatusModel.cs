using System.Text.Json;

namespace ShieldController;

internal enum ControllerState { NotPaired, Pairing, PairedDisconnected, Connected, Error }
internal sealed record AdapterChoice(string Guid, string Name) { public override string ToString() => Name; }
internal sealed record Requirement(string Id, string Label, bool Available);
internal sealed record Candidate(string TargetIdHash, string DisplayName) { public override string ToString() => DisplayName; }
internal sealed record Capabilities(bool Paired, bool RecoveryRequired, Requirement[] Requirements, AdapterChoice[] Wifi, AdapterChoice[] Uplink, string? SelectedWifi, string? SelectedUplink);

internal sealed class StatusModel
{
    internal ControllerState State { get; private set; } = ControllerState.NotPaired;
    internal bool Paired { get; private set; }
    internal bool TransportReady { get; private set; }
    internal string Message { get; private set; } = "Pair your controller to get started.";
    internal void ApplyCapabilities(Capabilities value)
    {
        Paired = value.Paired;
        if (value.RecoveryRequired) { Fail("The previous connection needs cleanup. Use Recover connection before connecting again."); return; }
        SetDisconnected();
    }
    internal void BeginPairing() { State = ControllerState.Pairing; TransportReady = false; Message = "Looking for the flashing controller…"; }
    internal void PairSucceeded() { Paired = true; SetDisconnected(); }
    internal void SetDisconnected(string? message = null)
    {
        TransportReady = false;
        State = Paired ? ControllerState.PairedDisconnected : ControllerState.NotPaired;
        Message = message ?? (Paired ? "Ready to connect. Tap the NVIDIA logo once to wake the controller." : "Pair your controller to get started.");
    }
    internal void ApplySession(JsonElement value)
    {
        // A network connection or a WPS callback is never gamepad readiness.
        bool connected = value.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        TransportReady = value.TryGetProperty("transport_ready", out var t) && t.ValueKind == JsonValueKind.True;
        if (!Paired) return;
        State = connected ? ControllerState.Connected : ControllerState.PairedDisconnected;
        Message = connected ? "Wireless gamepad input is active. Open your game and play."
            : TransportReady ? "Listening for your controller. Tap the NVIDIA logo once, then move a stick."
            : "Starting the wireless connection…";
    }
    internal void Fail(string message) { State = ControllerState.Error; TransportReady = false; Message = message; }
    internal static string Title(ControllerState state) => state switch {
        ControllerState.NotPaired => "Not paired", ControllerState.Pairing => "Pairing",
        ControllerState.PairedDisconnected => "Paired · disconnected", ControllerState.Connected => "Connected", _ => "Needs attention"
    };
}
