# Linux and macOS

| Platform | Available support |
|---|---|
| Windows 11 x64 | Main tray app and guided installer. See the [quick start](../README.md). |
| Ubuntu 24.04 x86_64 | **Linux preview** package using native shieldd and a supported dedicated USB Wi-Fi adapter. Hardware acceptance is still pending. |
| macOS | **Not supported by a release yet.** Native radio and gamepad integration still need implementation and verification. |

## Linux preview

[**Download the Ubuntu 24.04 preview (.deb)**](https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/releases/download/linux-preview-0.3.0/NVIDIA-SHIELD-P2570-Linux-Preview-0.3.0_amd64.deb)

The Linux package includes a compiled native backend, application-menu launcher, pairing prompt, device-permission rules and dependency metadata. A software installer or `apt` installs its required libraries without a separate dependency search. It targets Ubuntu 24.04 x86_64; it is not a universal Linux installer.

The backend is [shieldd](https://github.com/satyrnidae/shieldd/tree/9eb7f473c9b12c1190a4250463a99b3bf6fdfc37), pinned at `9eb7f473c9b12c1190a4250463a99b3bf6fdfc37`. Its native radio supports a **dedicated Realtek RTL8811AU/RTL8821AU USB adapter with ID `0bda:0811`**. Upstream reports testing with the ALFA AWUS036ACS. The adapter is reserved for the controller while the app runs. The tray requires a desktop with StatusNotifierItem support, such as KDE or GNOME with AppIndicator support.

This preview uses upstream's tray and features. It does not include the Windows app's mapping panel or promise matching desktop-button defaults. Build, launcher and package checks are run on Ubuntu under WSL; **physical Linux pairing, reconnecting, gamepad input and uninstall acceptance remain unverified**. WSL is only the build environment for this release. [Linux installation and build details](../platform/linux/README.md).

### Could ordinary built-in Wi-Fi work on Linux?

There is a credible path, but it is not implemented in this preview. Linux's `hostapd` supports AP mode through `nl80211` on capable kernel drivers, and supports WPS. A new backend could pair through that service, carry Ozmo frames through a raw Ethernet socket, and use the existing protocol engine plus `uinput` for gamepad output. This needs a radio lifecycle owner that preserves the user's normal network, firmware-specific pairing tests, and real adapter/channel validation. The dedicated-adapter limitation belongs to the packaged upstream radio backend; it is not a claim that Linux motherboard Wi-Fi is inherently impossible. [Linux Wireless AP documentation](https://wireless.docs.kernel.org/en/latest/en/users/documentation/hostapd.html), [hostapd features](https://w1.fi/hostapd/index.html), [Linux uinput documentation](https://docs.kernel.org/input/uinput.html).

## What remains for macOS

A `.dmg` alone cannot provide the controller's wireless transport. The Windows backend depends on Windows Mobile Hotspot/WCN and the Linux preview depends on Linux USB and input APIs. A Mac release needs:

1. A native radio backend that can perform the P2570's WPS pairing and carry its Ozmo frames. Possible approaches include porting the dedicated USB radio backend to a supported macOS USB API, or using an external radio bridge. Neither path is currently implemented or tested here. Apple's public CoreWLAN overview covers querying interfaces and joining networks; it does not provide this project's pairing/backend implementation. [CoreWLAN](https://developer.apple.com/documentation/corewlan).
2. A supported gamepad-output path and distribution configuration. Apple's `IOHIDUserDevice` can create virtual HID devices, and Apple's current open-source user-client implementation checks the virtual-HID entitlement. Simply compiling a helper or running it as root is not evidence that a normal end-user install can create the device. [Apple entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.hid.virtual.device), [Apple's implementation](https://github.com/apple-oss-distributions/IOHIDFamily/blob/main/IOHIDFamily/IOHIDResourceUserClient.cpp#L109-L147).
3. Real game compatibility, signing/notarization, permissions, pairing and reconnect tests on Intel and Apple Silicon Macs. Apple's Frameworks Engineer cautions that virtual gamepads may be technically possible through DriverKit or `IOHIDUserDevice`, but the Game Controller framework includes checks that ignore virtual HID devices, so compatibility cannot be assumed. [Apple's answer](https://developer.apple.com/forums/thread/812774).

These are implementation and validation gaps, not a claim that a Mac port is impossible. No placeholder Mac installer is published as though it could connect the controller.
