# Tray application

`ShieldController.exe` is the self-contained .NET 10 WinForms front end. The installer places it at `%LOCALAPPDATA%\NVIDIA-SHIELD-P2570-Windows\bin\tray\ShieldController.exe`.

- Normal launch and `--show` open the current user's existing instance, or start it. Startup connects only when the owner confirms a valid durable provisioning receipt and available requirements.
- `--exit` asks that instance to stop its owned wireless session, and waits up to 50 seconds for the process to exit. An unfinished cleanup leaves the application open and returns failure.
- Closing the window hides it in the notification area. Exit stops the owner first. Setup processes are never killed by the tray; an in-progress provisioning operation must reach its bounded cleanup.
- Pairing is explicit: discover, select one candidate, then pair it. The service owns identity validation, credentials and private provisioning receipts. The tray never receives credentials or raw controller addresses.
- “Connected” comes only from the owner's real Ozmo gamepad report flag. A successful pairing callback, network association, or listening transport is not displayed as connected.
- Test controller opens the standard Windows Game Controllers panel (`joy.cpl`). Controller bindings and sensitivity are configured in the game.
- Forget controller is available only while idle, with no pending recovery. It asks for confirmation before removing this app's saved pairing, allowing a different controller or adapter selection. It does not reset controller firmware.

The owner CLI and JSON protocol are centralized in `OwnerClient.cs`. No historical configuration parser or alternate connection implementation is present.

## Offline checks and captures

`--selftest` checks state transitions and malformed event draining without starting the owner or making radio calls. Because this is a Windows GUI executable, scripts must wait for its process and inspect its exit code.

`--render-preview-state NotPaired|Pairing|PairedDisconnected|Connected|Error --output <absolute-path-under-debug.png>` creates the actual production WinForms controls, leaves the top-level window hidden, and captures them with `Form.DrawToBitmap`. A JSON receipt records visibility, control state, image hash and zero owner calls. Preview state is injected into the same presentation model; these images verify the UI, not physical pairing or input.

Add `--viewport 700x650` to check a small outer window. The normal window fits the current monitor's working area; its content scrolls when the available height is shorter than the setup page.

Publish output and intermediate paths must be under the repository's `debug/` directory. The repository build script sets these paths and produces the installable package.
