# SHIELD Controller · Linux preview

[**Download the Ubuntu 24.04 preview (.deb)**](https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/releases/download/linux-preview-0.3.0/NVIDIA-SHIELD-P2570-Linux-Preview-0.3.0_amd64.deb)

This package provides the native Linux **shieldd** backend and a small guided launcher. It is a separate preview, not the Windows app or its custom mapping panel.

You need **Ubuntu 24.04 x86_64**, an original P2570, and a **dedicated RTL8811AU/RTL8821AU USB Wi-Fi adapter with USB ID `0bda:0811`**. The preview does not support built-in Wi-Fi. KDE shows the tray directly; GNOME needs StatusNotifierItem/AppIndicator support. Ubuntu's default desktop normally includes it.

1. Open the `.deb` in your system's software installer and choose **Install**. It fetches the required libraries automatically. If your desktop has no local-package installer, use `sudo apt install ./NVIDIA-SHIELD-P2570-Linux-Preview-<version>_amd64.deb`.
2. Open **SHIELD Controller (Linux preview)** from your applications.
3. Follow the prompt: unplug USB from the PC, hold the NVIDIA logo for three seconds, and choose **It's flashing**.

The tray handles pairing and reconnecting. The Xbox-style gamepad, touchpad mouse, media keys and controller audio come from upstream shieldd. This package does not promise the Windows control-mapping UI or identical defaults.

**Validation boundary:** the package is built and its offline tests run on Ubuntu 24.04 under WSL. WSL is a build environment here, not a supported way to play. This package still needs a physical Linux desktop/controller/adapter acceptance test. Do not treat a successful package install as proof of controller compatibility.

## Source and build

Upstream: [satyrnidae/shieldd](https://github.com/satyrnidae/shieldd), pinned by [`upstream.json`](upstream.json). The backend is GPL-2.0-only; its Realtek firmware redistribution notice is included in `UPSTREAM-NOTICES.md`. The matching source archive accompanies the `.deb`, including the locked Rust dependencies. The installed documentation includes the dependency license inventory and notices.

Build from this repository on Ubuntu 24.04 x86_64 with Rust stable:

```sh
sudo apt install build-essential pkg-config libclang-dev libgtk-4-dev libpipewire-0.3-dev git python3
bash platform/linux/build-deb.sh 0.3.0 "$PWD/debug/linux-preview-build"
```

The output directory must be new and under `debug/`. The builder pins and verifies the upstream commit, runs upstream tests and launcher/package checks, builds the binary, and produces a `.deb`, matching source archive, and checksums. It does not install the package or start the radio. To rebuild the backend from the source archive, run `cargo build --locked --release -p shieldd --target x86_64-unknown-linux-gnu --target-dir ../debug/build` from `shieldd-upstream/`; the dependencies above and Rust are required.

The package installs seat-scoped permissions for only the supported adapter and `/dev/uinput`. It does not start a root daemon or modify your normal Wi-Fi connection. Opening the app takes ownership of the dedicated USB adapter while it runs; quitting returns it to Linux. Uninstalling preserves your per-user pairing in `~/.config/shieldd`.
