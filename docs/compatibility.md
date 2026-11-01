# Compatibility and troubleshooting

The supported connection path uses Windows' own WPS provisioning and Mobile Hotspot APIs, then reads the P2570's Ozmo gamepad protocol through Npcap and exposes it through ViGEmBus. It does not port an RTL8811AU USB driver to other chips.

## What was established

On the initial RTL8852CE test PC, the controller associated with a 5 GHz/channel 36 network but repeatedly timed out during WPA2 key exchange after passphrase-based WPS provisioning. Provisioning the **same effective WPA2 key as a 64-character hexadecimal network key** completed security negotiation and produced real controller reports. This is the canonical pairing method in this app. The exact firmware interoperability cause was not established.

The controller used Ozmo firmware 81. The app's guided pairing runs on Windows without a firmware updater, Linux, USB device passthrough, Bluetooth pairing, or a dedicated RTL8811AU adapter.

## Current boundaries

- Windows 11 24H2+ APIs and x64 are required. Windows 10 and ARM64 are not release targets.
- This release requires exactly one enabled Wi-Fi adapter. The WCN pairing provider does not expose a reliable adapter-selection constraint, so an ambiguous radio setup is rejected.
- Only actual 5 GHz channel 36 has been verified. If Windows chooses a different channel, the app stops with a diagnostic instead of claiming success.
- Windows Mobile Hotspot requires an eligible uplink. Ethernet is the tested choice. Sharing an existing Wi-Fi connection depends on the adapter and driver and is not independently verified.
- The app must own the hotspot session. Stop an already running hotspot before connecting.
- A hotspot's IP-client count can remain zero for this controller. That count is not used to decide whether the controller is connected; real Ozmo gamepad reports are required.
- A reported session disconnect neutralizes the virtual pad. If the connection disappears silently, the host also neutralizes input after about one second without accepted controller traffic.

## Recovery

Use the app's recovery action if a previous process ended before network cleanup completed. It checks the recorded session before acting. Do not delete recovery files while a session is active.

If Windows or the driver refuses cleanup, open the local logs for the failed checkpoint and use **Recover connection** when offered. Pairing data is preserved. Rebooting is an explicit user action, never an automatic app action.

## Driver distribution

[Npcap's official licence](https://npcap.com/oem/redist) restricts redistribution and silent installation of the free edition. This project does not bundle Npcap. Its helper downloads an unmodified official installer directly, verifies the pinned SHA-256 and Authenticode publisher, then opens its interactive setup. Enterprises must check their own Npcap licence requirements.

ViGEmBus is BSD-3-Clause licensed and archived upstream. The app uses the final official signed 1.22.0 installer, which removed its old updater. The app does not uninstall shared drivers.
