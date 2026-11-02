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

The maintained C# owner, launch/configuration scripts and runtime composition
are in this directory alongside the corresponding source. Keep this notice,
the license and corresponding modified source with any distribution of the
controller package. The package builder does not bundle Npcap or ViGEm drivers;
those are separately installed prerequisites.
