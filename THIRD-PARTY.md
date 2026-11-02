# Third-party source

The separate controller data-host executable and its Ozmo protocol dependency
use GPL-2.0-only source from [shieldd](https://github.com/satyrnidae/shieldd),
by Isabel Maskrey, commit `7ec29f44a03a074c7f5fdb7306815e7f404a572e`.
The complete license is included as `LICENSE`.

- `vendor/ozproto/src/{lib,session,usb,wire}.rs` comes from upstream `ozproto/src`.
  Its package metadata is owned by this standalone workspace; formatting may
  differ, while the protocol implementation is retained.
- `data-host/src/{adapters,controller,npcap}.rs` derives from upstream Windows
  transport/controller modules. The active host uses one exact controller,
  gamepad, touchpad mouse, arrow-key and Windows volume output, bounded queues,
  disabled payload tracing, and supervised lifetime. It does not forward
  headset audio, microphone or rumble output.
- `data-host/src/mapping.rs` retains the upstream controller-to-XInput mapping.
- `data-host/src/firmware.rs` implements the P2570 firmware response layout
  documented and checked against NVIDIA's display in upstream
  `shdlib/src/firmware.rs`, commit `9eb7f473c9b12c1190a4250463a99b3bf6fdfc37`.
  No additional dependency or firmware binary is included.
- The independently implemented Nova Pro Wireless volume control uses protocol
  facts documented in [GGWP's findings](https://github.com/hughes/GGWP/blob/main/FINDINGS.md).
  Only the settings query and bounded headphone-volume command are used; no
  device-flash, firmware, or audio-routing commands are sent.

The maintained C# owner, launch/configuration scripts and runtime composition
are in this directory alongside the corresponding source. Keep this notice,
the license and corresponding modified source with any distribution of the
controller package.

The Windows installer includes the unmodified, signed ViGEmBus 1.22.0 installer
under its BSD-3-Clause licence; its notice is included in
`installer/licenses/ViGEmBus.txt` and the installed `licenses` folder. Npcap is
not bundled: setup downloads the official file directly and leaves its licence
approval visible.

The separate Linux preview uses the pinned native Linux shieldd implementation
identified in `platform/linux/upstream.json`. Each Linux release includes its
corresponding source archive and upstream notices. It has different hardware
requirements and is not the Windows application running through a compatibility
layer.
