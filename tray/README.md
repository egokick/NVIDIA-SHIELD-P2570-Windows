# Tray application

`ShieldController.exe` is the self-contained .NET 10 WinForms front end. The installer places it at `%LOCALAPPDATA%\NVIDIA-SHIELD-P2570-Windows\bin\tray\ShieldController.exe`.

- Normal launch and `--show` open the current user's existing instance, or start it. Startup connects only when the owner confirms a valid durable provisioning receipt and available requirements.
- `--exit` asks that instance to stop its owned wireless session, and waits up to 50 seconds for the process to exit. An unfinished cleanup leaves the application open and returns failure.
- Closing the window hides it in the notification area. Exit stops the owner first. Setup processes are never killed by the tray; an in-progress provisioning operation must reach its bounded cleanup.
- The main screen has a controller illustration and one primary action. After unplugging USB from the PC and holding the touch-sensitive NVIDIA logo for three seconds, **It's flashing** discovers and pairs the sole candidate, rechecks the durable pairing receipt and requirements, then starts the connection automatically. Multiple candidates require an explicit choice; the app never picks the first of several controllers. The service owns identity validation, credentials and private provisioning receipts. The tray never receives credentials or raw controller addresses.
- **Wake up & play** means the pairing is saved and the transport is listening. **Ready to play** and the tray's **Connected** status come only from the owner's real Ozmo gamepad report flag. A successful pairing callback, network association, or listening transport is not displayed as connected.
- **Done**, **Hide to tray**, and the window close button hide the window without stopping the session. Clicking the tray icon reopens it.
- **Settings** holds adapter choices, requirement details, setup checks, Connect/Disconnect, Forget controller, and logs. Saved selections or a sole available adapter are selected automatically; an ambiguous choice requires the user. Missing drivers are presented as one focused installation step at a time. Official driver installers still require their own prompts and any Windows approval.
- Test controller opens the standard Windows Game Controllers panel (`joy.cpl`). Controller bindings and sensitivity are configured in the game.
- Forget controller is available only while idle, with no pending recovery. It asks for confirmation before removing this app's saved pairing, allowing a different controller or adapter selection. It does not reset controller firmware.

The owner CLI and JSON protocol are centralized in `OwnerClient.cs`. No historical configuration parser or alternate connection implementation is present.

## Offline checks and captures

`--selftest` checks state transitions, malformed event draining, and the discovery/pairing sequence with a fake service, without starting the owner or making radio calls. Checks cover zero, one, and multiple candidates, failure propagation, and the durable-pairing/requirements gates before connection. Because this is a Windows GUI executable, scripts must wait for its process and inspect its exit code.

`--render-preview-state <scene> --output <absolute-path-under-debug.png>` creates the actual production WinForms controls, leaves the top-level window hidden, and captures them with `Form.DrawToBitmap`. A JSON receipt records visibility, control state, image hash and zero owner calls. Scenes are `NotPaired`, `Pairing`, `Provisioning`, `PairedDisconnected`, `Connected`, `Error`, `MissingDriver`, `Recovery`, `MultipleControllers`, `Settings`, `Help`, `NoController`, `Connecting`, `RestartRequired`, `InstallFailed`, and `NetworkChoice`. Preview state is injected into the same presentation model; these images verify the UI, not physical pairing or input.

Add `--viewport 440x480` to check the minimum outer window. The normal window fits the current monitor's working area; its content scrolls when the available height is shorter than the page. When scrolling is needed, the preview also captures the bottom of the page.

Publish output and intermediate paths must be under the repository's `debug/` directory. The repository build script sets these paths and produces the installable package.
