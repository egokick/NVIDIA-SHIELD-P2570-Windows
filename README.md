# NVIDIA SHIELD P2570 for Windows

Use the **original NVIDIA SHIELD P2570 controller wirelessly** as an Xbox-compatible gamepad on Windows. A small tray app guides you through pairing and shows whether the controller is paired, waiting to connect, or connected.

**[Download the Windows installer](https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/releases/latest)**

This is an independent community project, not an NVIDIA product. The P2570 uses **Wi-Fi**, not Bluetooth. This app is for the original P2570, not the later Bluetooth SHIELD controller.

## Get started

1. Run **NVIDIA-SHIELD-P2570-Windows-Setup-0.1.0.exe** and click **Install**.
2. Open the tray app. If a driver is missing, use its installation button and finish the official driver's setup prompts.
3. Select the Wi-Fi and Internet adapters in the app. Unplug the controller's USB cable from the PC; a wall USB charger is fine. Rest your fingertip on the green NVIDIA logo for about **three seconds**, until it flashes. The logo is touch sensitive; it does not click.
4. Click **Find controller**, select the discovered controller, then click **Pair selected controller**. Wait for pairing to finish.
5. Click **Connect**, briefly touch the NVIDIA logo to wake the controller, and move a stick. Once the status reads **Connected**, open your game and choose its Xbox/controller input.

After pairing, a short touch on the NVIDIA logo wakes the controller. Keep the tray app running while you play. **Disconnect** stops the connection; **Exit** closes the app and restores the network settings it temporarily changed.

The app installer needs no .NET or developer tools. Driver installation may require a Windows administrator prompt. Npcap is installed separately through its official interactive setup. The app downloads the official driver installers directly and verifies their signatures and hashes before opening them.

## Requirements and tested hardware

- **Windows 11 24H2 or later, x64.** The app checks for the required Windows networking APIs.
- An original **NVIDIA SHIELD P2570** with sufficient battery charge.
- A Wi-Fi adapter with Windows Mobile Hotspot support and **5 GHz channel 36** available. Attach the PC's Wi-Fi antenna.
- **Exactly one enabled Wi-Fi adapter.** Disable additional Wi-Fi adapters while using this release; Bluetooth adapters do not count.
- An active Internet-capable network connection for Windows Mobile Hotspot. **Ethernet is recommended.**
- **[Npcap](https://npcap.com/)** and **[ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0)**. Setup checks for these and provides installation buttons. Leave Npcap's administrator-only option unchecked so the tray app can run normally.

The working wireless path has been tested with **P2570 firmware Ozmo 81, Realtek RTL8852CE motherboard Wi-Fi, and Windows 11 build 26300**. Actual stick movement and button presses reached Windows XInput. This does **not** establish compatibility with every adapter, Windows build, or controller firmware. Unsupported capabilities produce an error instead of changing driver settings.

This first release supports one controller and the standard gamepad sticks, buttons, D-pad and triggers. Audio, microphone, touchpad mouse input and vibration are not implemented. ViGEmBus is an archived upstream project; its final signed release is used here.

## What the tray status means

| Status | Meaning |
|---|---|
| Not paired | Complete the pairing guide first. |
| Pairing | The app is searching for a controller or provisioning the one you selected. |
| Paired · disconnected | Pairing is saved. The description says whether the app is stopped, starting, or listening for input. |
| Connected | The selected controller is sending actual gamepad reports to Windows. |
| Needs attention | Read the setup guidance, use Refresh, or open logs for the failed operation. Recover connection appears when cleanup is pending. |

Pairing alone is never reported as a live connection. The app only accepts the selected controller's network identity and creates one virtual Xbox controller.

**Test controller** opens Windows' standard Game Controllers panel. Set controller bindings and sensitivity in your game. To change the saved controller or network adapters, disconnect, choose **Forget controller…** from the tray menu, confirm, and pair again. This removes the app's saved pairing; it does not reset controller firmware.

## If it will not pair

- Charge the controller first. The small light beside its USB socket is a charging light; watch the **NVIDIA logo itself** for pairing.
- Unplug USB from the PC before wireless pairing. A wall charger can supply power without a USB data connection.
- If the logo will not flash, hold it for **15 seconds**, release, wait five seconds, then hold it for about three seconds again.
- Close other mobile hotspots or Wi-Fi Direct apps and retry.
- If the app reports an unsupported channel or adapter, see [compatibility and troubleshooting](docs/compatibility.md). Do not install random replacement Wi-Fi drivers or disable driver signing.

Uninstall through **Windows Settings → Apps**. Uninstalling keeps shared Npcap/ViGEmBus drivers and your private pairing details, so it does not break other applications.

## Privacy and permissions

No account, telemetry or cloud service is used for pairing or play. Pairing credentials stay in your Windows profile with access restricted to your account. The app temporarily starts its own hotspot and restores the prior idle-timeout policy when it stops. It does not register a system service, flash controller firmware, or replace the controller's USB driver.

The release is currently **unsigned**; Windows may show an unknown-publisher warning. Download only from this repository's Releases page and compare the included SHA-256 checksum if needed.

## Build and contribute

See [BUILDING.md](BUILDING.md) for the Windows build, tests and installer. Please include your Windows build, Wi-Fi adapter/driver, controller model and the app's error stage when reporting an issue. **Do not post pairing configuration files, Wi-Fi passwords or raw packet captures.**

The source is licensed under **GPL-2.0-only**. The protocol implementation builds on [satyrnidae/shieldd](https://github.com/satyrnidae/shieldd); see [THIRD-PARTY.md](THIRD-PARTY.md) and the bundled notices for attribution and dependency licences. Npcap and ViGEmBus remain separately installed upstream components.
