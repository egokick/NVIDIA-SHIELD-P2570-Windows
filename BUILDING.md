# Building on Windows

Required: Windows 11 x64, .NET 10 SDK, Rust stable with the MSVC target, Visual Studio C++ Build Tools and Windows SDK. Runtime users do not need these tools.

```powershell
./build.ps1
./installer/build-installer.ps1
```

The build downloads the official Npcap SDK for its import library; it does not put Npcap drivers or DLLs in the release. All build intermediates, dependency downloads, package staging and generated evidence belong in `debug/`, which Git ignores.

The package is staged in `debug/package/`. The standalone setup executable and checksum are written to `debug/release/`. The installer embeds the package and installs it for the current user, without requiring a separately installed .NET runtime.

The installer build downloads and verifies the signed BSD-licensed ViGEmBus
installer, includes its licence, and embeds it in the package. Npcap remains an
official download inside the guided setup because its free licence does not
permit redistribution or silent installation. The stable direct-download asset
is `SHIELD-Controller-Setup.exe`; it is byte-identical to the versioned installer.

Native Linux preview packaging and its separate build requirements are in
[`platform/linux/README.md`](platform/linux/README.md). Platform support and
remaining macOS work are documented in [`docs/platforms.md`](docs/platforms.md).

Package staging must be a new directory. For another build, choose a fresh path with `./build.ps1 -OutputDirectory debug/package-next`, then pass that same path to `./installer/build-installer.ps1 -PackagePath debug/package-next`.

## Source layout

- `owner/`: Windows hotspot lifecycle, setup, WPS pairing and recovery.
- `data-host/`: exact-controller Ozmo transport, virtual Xbox gamepad, mouse and keyboard output.
- `vendor/ozproto/`: protocol code with upstream licence/provenance.
- `tray/`: Windows tray app, guided pairing, requirements and live status.
- `installer/`: installer, uninstaller and verified official-driver download helper.
- `contracts/default-controls.json`: shared C#/Rust desktop-mapping defaults fixture.

Keep personal credentials, controller identifiers, local logs and test captures out of commits and release archives. A release must contain source/licence notices matching its binaries. Update `THIRD-PARTY.md` and regenerate notices whenever dependencies change.

## Verification

Run the build's offline tests before creating an installer. The owner and tray expose self-test modes; the setup executable has `--selftest`. Hardware tests must additionally cover real pairing, actual stick/button data, connection loss, reconnect, normal exit and restoration of the prior hotspot idle policy. A successful build or a pairing callback alone does not prove wireless input.

For installer checks on an existing test machine, `Setup.exe --silent --no-launch` installs the package without opening the tray. This does not silently install drivers. The normal release UI is opened by running Setup without arguments.

Release binaries are currently unsigned. Do not claim a Windows signing identity or hardware coverage that has not been verified.
