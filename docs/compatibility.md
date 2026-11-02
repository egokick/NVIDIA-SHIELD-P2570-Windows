# Compatibility and troubleshooting

The supported connection path uses Windows' own WPS provisioning and Mobile Hotspot APIs, then reads the P2570's Ozmo gamepad protocol through Npcap and exposes it through ViGEmBus. It does not port an RTL8811AU USB driver to other chips.

## What was established

On the initial RTL8852CE test PC, the controller associated with a 5 GHz/channel 36 network but repeatedly timed out during WPA2 key exchange after passphrase-based WPS provisioning. Provisioning the **same effective WPA2 key as a 64-character hexadecimal network key** completed security negotiation and produced real controller reports. This is the canonical pairing method in this app. The exact firmware interoperability cause was not established.

The controller used Ozmo firmware 81. The app's guided pairing runs on Windows without a firmware updater, Linux, USB device passthrough, Bluetooth pairing, or a dedicated RTL8811AU adapter.

## Current boundaries

**Release status:** stable Windows v0.3.0 uses 5 GHz/channel 36. The band selector and automatic selection below are development features. The test PC can create a 2.4 GHz hotspot, but the controller has not yet connected and delivered input over that path. The dual-band release remains unpublished until that test passes.

- Windows 11 24H2+ APIs and x64 are required. Windows 10 and ARM64 are not release targets.
- This release requires exactly one enabled Wi-Fi adapter. The WCN pairing provider does not expose a reliable adapter-selection constraint, so an ambiguous radio setup is rejected.
- **Settings → Controller Wi-Fi band** offers **Automatic (prefer 5 GHz)**, **5 GHz only**, and **2.4 GHz only**. Automatic checks Windows' band support and chooses 5 GHz first. If Windows reports a radio restriction or band interference while starting 5 GHz, it retries 2.4 GHz only after verifying that no hotspot started. Explicit choices never switch bands silently. Changes apply on the next connection and preserve your pairing.
- Supported controller channels are **1–11** on 2.4 GHz and **36/40/44/48** on 5 GHz. Windows chooses the channel within the requested band; the app verifies the actual channel before accepting input. Other channels produce a diagnostic. An unknown, pending or already-running hotspot is never treated as a reason to retry on another band.
- The controller connects directly to the PC. Your router's Wi-Fi band does not need to match the controller link. Using Wi-Fi for both the Internet uplink and controller link still depends on the PC adapter and driver; Ethernet is the tested uplink.
- Windows Mobile Hotspot requires an eligible uplink. Ethernet is the tested choice. Sharing an existing Wi-Fi connection depends on the adapter and driver and is not independently verified.
- The app must own the hotspot session. Stop an already running hotspot before connecting.
- A hotspot's IP-client count can remain zero for this controller. That count is not used to decide whether the controller is connected; real Ozmo gamepad reports are required.
- A reported session disconnect neutralizes the virtual pad. If the connection disappears silently, the host also neutralizes input after about one second without accepted controller traffic.

## Recovery

Use the app's recovery action if a previous process ended before network cleanup completed. It checks the recorded session before acting. Do not delete recovery files while a session is active.

If Windows or the driver refuses cleanup, open the local logs for the failed checkpoint and use **Recover connection** when offered. Pairing data is preserved. Rebooting is an explicit user action, never an automatic app action.

## Driver distribution

[Npcap's official licence](https://npcap.com/oem/redist) restricts redistribution and silent installation of the free edition. This project does not bundle Npcap. Its helper downloads an unmodified official installer directly, verifies the pinned SHA-256 and Authenticode publisher, then opens its interactive setup. Enterprises must check their own Npcap licence requirements.

ViGEmBus is BSD-3-Clause licensed and archived upstream. Setup includes the final official signed 1.22.0 installer and its licence, and installs it automatically after Windows approval. This version removed its old updater. The app does not uninstall shared drivers.

Setup verifies both drivers can be opened before declaring them ready. If Windows
blocks Wi-Fi access, the app opens the relevant Location settings page and checks
again when you return. It never enables permissions or reboots Windows for you.
