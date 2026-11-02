# Documentation screenshots

These are maintained product documentation, captured from the actual Windows tray application's production controls. The built-in preview selects representative UI states without starting a hotspot, reading pairing credentials, injecting input, or showing a desktop window.

- `pair-controller.png`: `NotPaired` scene, 536 × 689.
- `your-controls.png`: `Connected` scene with joystick controls, 1040 × 1120 (`--viewport 1040x1120`).

To refresh them after changing the UI, run the packaged tray executable with `--render-preview-state NotPaired --output <repository>/debug/<new-run>/NotPaired.png` and again with `Connected`. Review the images and their JSON receipts before replacing these two documentation assets. Keep receipts and scratch captures under ignored `debug/`; only the reviewed documentation PNGs belong here.

The images show UI states, not evidence of a new hardware compatibility test.
