#!/usr/bin/python3
"""Guided launcher for the pinned native shieldd Linux preview.

The launcher never runs as root, downloads code, changes network settings, or
executes a shell. The upstream radio process opens only its supported adapter.
"""
from __future__ import annotations

import argparse
import fcntl
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

BINARY = Path("/usr/lib/nvidia-shield-p2570/shieldd")
ICON = Path("/usr/share/icons/hicolor/256x256/apps/nvidia-shield-p2570.png")
TITLE = "SHIELD Controller · Linux preview"


def adapters(sysroot: Path = Path("/sys/bus/usb/devices")) -> list[Path]:
    found = []
    for entry in sorted(sysroot.glob("*")):
        try:
            if (entry / "idVendor").read_text().strip() == "0bda" and (entry / "idProduct").read_text().strip() == "0811":
                bus = int((entry / "busnum").read_text().strip())
                device = int((entry / "devnum").read_text().strip())
                if 1 <= bus <= 999 and 1 <= device <= 999:
                    found.append(Path(f"/dev/bus/usb/{bus:03d}/{device:03d}"))
        except (OSError, ValueError):
            continue
    return found


def preflight(devices: list[Path], access=os.access, uinput: Path = Path("/dev/uinput")) -> str | None:
    if not devices:
        return "Plug in a supported USB Wi-Fi adapter, then open SHIELD Controller again.\n\nThis Linux preview needs an RTL8811AU / RTL8821AU adapter with USB ID 0bda:0811. Built-in Wi-Fi is not supported by this preview."
    if len(devices) != 1:
        return "Leave one supported SHIELD USB Wi-Fi adapter connected, then open the app again."
    if not access(devices[0], os.R_OK | os.W_OK):
        return "Unplug the USB Wi-Fi adapter, plug it back in, then open the app again.\n\nUse your normal local desktop login, not a root or remote session."
    if not access(uinput, os.R_OK | os.W_OK):
        return "Restart Linux once to finish controller setup, then open the app again.\n\nThis preview needs a native Linux desktop with the uinput kernel module. WSL is not a supported play environment."
    return None


def message(text: str, kind: str = "info", runner=subprocess.run) -> int:
    return runner(["zenity", f"--{kind}", "--title", TITLE, "--text", text, "--no-markup", "--width", "460"], check=False).returncode


def pair_prompt(runner=subprocess.run) -> bool:
    return runner(["zenity", "--question", "--title", TITLE, "--text",
                   "Unplug the controller from your PC.\n\nTouch and hold its green NVIDIA logo for 3 seconds, until it flashes.",
                   "--no-markup", "--width", "460", "--ok-label", "It's flashing", "--cancel-label", "Not now"], check=False).returncode == 0


def settings_directory() -> Path:
    return Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "shieldd"


def has_saved_controller(directory: Path) -> bool:
    try:
        names = directory / "known-clients"
        if not (directory / "p2p.conf").is_file() or names.stat().st_size > 65536:
            return False
        return any(line.strip() and not line.lstrip().startswith("#") for line in names.read_text().splitlines())
    except OSError:
        return False


def running(directory: Path) -> bool:
    lock = Path(os.environ.get("XDG_RUNTIME_DIR", directory)) / "shieldd.lock"
    try:
        with lock.open("rb") as handle:
            try:
                fcntl.flock(handle.fileno(), fcntl.LOCK_SH | fcntl.LOCK_NB)
                return False
            except BlockingIOError:
                return True
    except FileNotFoundError:
        return False


def host(pair: bool, binary: Path = BINARY, runner=subprocess.run) -> int:
    # No arbitrary command string, shell, elevation, or argument forwarding.
    return runner([str(binary)] + (["--pair"] if pair else []), check=False,
                  stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=TITLE)
    parser.add_argument("--pair", action="store_true")
    parser.add_argument("--doctor", action="store_true", help="Read-only checks; does not start the radio")
    args = parser.parse_args()
    devices = adapters()
    problem = preflight(devices)
    if args.doctor:
        print(json.dumps({"platform": sys.platform, "architecture": platform.machine(), "supported_adapter_count": len(devices),
                          "uinput_available": os.access("/dev/uinput", os.R_OK | os.W_OK), "ready": problem is None,
                          "radio_calls": 0, "preview": True}))
        return 0 if problem is None else 1
    if os.geteuid() == 0:
        print("Open SHIELD Controller from your normal desktop account, not as root.", file=sys.stderr)
        return 1
    if problem:
        message(problem, "error")
        return 1
    directory = settings_directory()
    if running(directory):
        message("SHIELD Controller is already running.\n\nUse its tray icon to pair another controller or quit.")
        return 0
    pair = args.pair or not has_saved_controller(directory)
    if pair and not pair_prompt():
        return 0
    result = host(pair)
    if result:
        message("The controller connection stopped.\n\nReconnect the USB Wi-Fi adapter and try again. Diagnostic logs are in your user configuration folder under shieldd/logs.", "error")
    return result


if __name__ == "__main__":
    raise SystemExit(main())
