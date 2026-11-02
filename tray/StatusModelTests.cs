using System.Text.Json;

namespace ShieldController;

internal static class StatusModelTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool success, string message) { checks++; if (!success) throw new InvalidOperationException(message); }
        static void Session(StatusModel model, string json)
        {
            using var value = JsonDocument.Parse(json);
            model.ApplySession(value.RootElement);
        }
        const string detected = "{\"transport_ready\":true,\"controller_detected\":true}";
        var model = new StatusModel();
        Session(model, detected);
        Check(!model.ControllerDetected && model.State == ControllerState.NotPaired, "An unpaired controller cannot become detected from a session event.");
        model.PairSucceeded();
        Check(!model.ControllerDetected, "Saving a pairing is not wireless detection.");
        Session(model, "{\"transport_ready\":true}");
        Check(model.TransportReady && !model.ControllerDetected && !model.Message.Contains("joystick"), "A receiver-ready event without detection must wait for the controller.");
        Session(model, "{\"transport_ready\":true,\"controller_detected\":false}");
        Check(!model.ControllerDetected && model.Message.Contains("connect wirelessly"), "An imported pairing with a ready receiver must not prompt for joystick input.");
        Session(model, detected);
        Check(model.ControllerDetected && model.State == ControllerState.PairedDisconnected && model.Message.Contains("joystick"), "Only a detected controller waiting for input should prompt for movement.");
        Session(model, "{\"transport_ready\":true,\"controller_detected\":true,\"connected\":true}");
        Check(model.ControllerDetected && model.State == ControllerState.Connected, "Valid gamepad input advances a detected controller to connected.");
        Session(model, "{\"transport_ready\":true,\"controller_detected\":false}");
        Check(!model.ControllerDetected && model.State == ControllerState.PairedDisconnected && !model.Message.Contains("joystick"), "A released controller session must stop asking for input.");
        Session(model, detected);
        Session(model, "{\"transport_ready\":false,\"controller_detected\":true}");
        Check(!model.ControllerDetected, "Controller detection cannot survive loss of its receiver.");
        foreach (string value in new[] { "null", "\"true\"", "1", "{}" })
        {
            Session(model, detected);
            Session(model, "{\"transport_ready\":true,\"controller_detected\":" + value + "}");
            Check(!model.ControllerDetected, "Only the boolean true confirms a detected controller.");
        }
        Session(model, detected); model.SetDisconnected();
        Check(!model.ControllerDetected && !model.TransportReady, "Stopping the session clears detection.");
        Session(model, detected); model.Fail("Connection failed.");
        Check(!model.ControllerDetected && !model.TransportReady, "A failed session clears detection.");
        Session(model, detected); model.BeginPairing();
        Check(!model.ControllerDetected && !model.TransportReady, "Starting pairing clears the previous detection.");
        Session(model, detected); model.ApplyCapabilities(new(false, false, [], [], [], null, null));
        Check(!model.ControllerDetected && !model.Paired, "Forgetting the pairing clears detection.");
        Check(StatusModel.Title(ControllerState.PairedDisconnected) == "Pairing saved · disconnected", "The tray status must distinguish saved pairing from an active connection.");
        return checks;
    }
}
