# Compatibility and troubleshooting

The supported connection path uses Windows' own WPS provisioning and Mobile Hotspot APIs, then reads the P2570's Ozmo gamepad protocol through Npcap and exposes it through ViGEmBus. It does not port an RTL8811AU USB driver to other chips.

## What was established

On the initial RTL8852CE test PC, the controller associated with a 5 GHz/channel 36 network but repeatedly timed out during WPA2 key exchange after passphrase-based WPS provisioning. Provisioning the **same effective WPA2 key as a 64-character hexadecimal network key** completed security negotiation and produced real controller reports. This is the canonical pairing method in this app. The exact firmware interoperability cause was not established.

The controller used Ozmo firmware 81. The app's guided pairing runs on Windows without a firmware updater, Linux, USB device passthrough, Bluetooth pairing, or a dedicated RTL8811AU adapter.

On the ASUS GA403WR laptop with a MediaTek MT7925 adapter, Windows reported 5 GHz support but the initial WCN discovery network used 2.4 GHz. A controlled test created and verified a temporary 5 GHz network on channel 149 and WCN accepted a full 90-second discovery attempt, but found no controller. A separate diagnostic test on verified channel 36 also completed 90 seconds without finding the controller. These tests establish that the laptop can create a supported 5 GHz network; successful controller pairing and input on this adapter remain unresolved.

## Current boundaries

**Controller input currently requires 5 GHz**, using channels **36, 40, 44, 48, 149, 153, 157, 161 or 165**. The test PC could create a 2.4 GHz hotspot but did not deliver controller input over it. Temporary WCN discovery and pairing are allowed on 2.4 or 5 GHz. After pairing, the app creates a separate 5 GHz input network and verifies its actual channel; it never falls back to 2.4 GHz for input.

The upper channels are listed for the P2570 in its [FCC radio test report, printed pages 9–11](https://fccid.io/m/061c4660adb2b9265a5dea9c371d3bebd22cb9f7a73a320571ff568228f2de49.pdf#page=10). The app accepts those channels as well as the lower channels; the original successful controller-input test used channel 36. Windows, the adapter driver and the local channel rules still determine which network can be created.

- Windows 11 24H2+ APIs and x64 are required. Windows 10 and ARM64 are not release targets.
- This release requires exactly one enabled Wi-Fi adapter. The WCN pairing provider does not expose a reliable adapter-selection constraint, so an ambiguous radio setup is rejected.
- **Settings → Controller input band** displays **5 GHz only** and explains that temporary pairing can use 2.4 or 5 GHz. Old Automatic or 2.4 GHz preferences cannot authorize a lower-band input connection. Pairing credentials are preserved.
- Pairing uses the native WCN discovery network, whose band Windows chooses. The app verifies the selected interfaces and a single stable group-owner interface before provisioning. A 2.4 GHz bootstrap network is permitted. If Windows returns `ERROR_INVALID_STATE` (5023) specifically for that interface's channel query, pairing may continue with the channel recorded as unknown; other query failures and known unsupported channels remain failures. The experimental prestarted 5 GHz hotspot path is not used for setup.
- Temporary-pairing-network verification failures are reported separately from input-network failures. Missing or ambiguous networks and unreadable channel queries do not establish that the adapter lacks 5 GHz. **5 GHz connection unavailable** describes the input network; **5 GHz required** is used when Windows explicitly reports that the adapter cannot create a 5 GHz network.
- Controller input explicitly requests 5 GHz and verifies the actual channel and SSID before launching the data host. Only **36/40/44/48/149/153/157/161/165** are accepted. An unknown, pending or already-running hotspot is never treated as a reason to try another band.
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
