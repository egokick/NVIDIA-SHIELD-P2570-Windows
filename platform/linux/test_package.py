"""Inspect package and run maintainer scripts against command recorders only."""
import json
import os
from pathlib import Path
import subprocess
import sys
import stat

out = Path(sys.argv[1]).resolve()
if "debug" not in out.parts:
    raise SystemExit("Test artifacts must be under debug/.")
stage = out / "stage"
# Debian payloads must never inherit DrvFS's permissive emulated modes.
for entry in stage.rglob("*"):
    if entry.is_symlink():
        continue
    assert not (stat.S_IMODE(entry.stat().st_mode) & 0o022), f"Writable package path: {entry}"
for name in ["postinst", "postrm"]:
    assert stat.S_IMODE((stage / "DEBIAN" / name).stat().st_mode) == 0o755
assert (stage / "usr/lib/nvidia-shield-p2570/shieldd").read_bytes()[:4] == b"\x7fELF"
control = (stage / "DEBIAN/control").read_text()
for dependency in ["libgtk-4-1 (>= 4.10)", "libpipewire-0.3-0t64", "python3", "zenity", "udev", "kmod"]:
    assert dependency in control
rules = (stage / "usr/lib/udev/rules.d/70-nvidia-shield-p2570.rules").read_text()
assert 'ATTR{idVendor}=="0bda"' in rules and 'ATTR{idProduct}=="0811"' in rules
assert "uaccess" in rules and "MODE=" not in rules
assert "--pair" in (stage / "usr/share/applications/nvidia-shield-p2570.desktop").read_text()
inventory = json.loads((stage / "usr/share/doc/nvidia-shield-p2570-linux-preview/THIRD-PARTY-CRATES.json").read_text())
assert inventory and all(item["source_in_archive"] for item in inventory)
assert (out / "source/shieldd-upstream/vendor-cargo").is_dir()
stubs = out / "maintainer-stubs"
stubs.mkdir()
log = out / "maintainer-actions.txt"
for name in ["modprobe", "udevadm"]:
    executable = stubs / name
    executable.write_text('#!/bin/sh\nprintf "%s" "' + name + '" >> "$SHIELD_TEST_LOG"\nprintf " %s" "$@" >> "$SHIELD_TEST_LOG"\nprintf "\\n" >> "$SHIELD_TEST_LOG"\n')
    executable.chmod(0o755)
env = os.environ | {"PATH": str(stubs), "SHIELD_TEST_LOG": str(log)}
subprocess.run(["/bin/sh", str(stage / "DEBIAN/postinst"), "configure"], env=env, check=True)
subprocess.run(["/bin/sh", str(stage / "DEBIAN/postrm"), "remove"], env=env, check=True)
assert log.read_text().splitlines() == [
    "modprobe uinput", "udevadm control --reload-rules",
    "udevadm trigger --action=change --subsystem-match=misc --sysname-match=uinput",
    "udevadm trigger --action=change --subsystem-match=usb --attr-match=idVendor=0bda --attr-match=idProduct=0811",
    "udevadm control --reload-rules",
]
receipt = {"passed": True, "package_inspection": True, "maintainer_commands_verified": 5,
           "package_permissions_checked": True, "source_packages": len(inventory),
           "radio_calls": 0, "driver_changes": 0, "hardware_validation": "pending"}
(out / "package-checks.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt))
