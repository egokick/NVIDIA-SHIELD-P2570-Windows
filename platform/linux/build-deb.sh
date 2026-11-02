#!/usr/bin/env bash
# Build the native Linux preview; never install it or start a radio during build.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
version=${1:?Usage: build-deb.sh VERSION NEW_DEBUG_OUTPUT_DIRECTORY}
out=${2:?Usage: build-deb.sh VERSION NEW_DEBUG_OUTPUT_DIRECTORY}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Use a numeric release version.' >&2; exit 2; }
out=$(python3 - "$root" "$out" <<'PY'
from pathlib import Path
import sys
root, out = map(lambda p: Path(p).resolve(), sys.argv[1:])
if not out.is_relative_to(root / 'debug') or out == root / 'debug' or out.exists():
    raise SystemExit('Output must be a new directory under repository debug/.')
print(out)
PY
)
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'Build on Linux x86_64.' >&2; exit 2; }
for tool in cargo rustc git pkg-config dpkg-deb python3 gcc; do command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 2; }; done
pkg-config --atleast-version=4.10 gtk4 || { echo 'GTK 4.10 development files are required.' >&2; exit 2; }
pkg-config --exists libpipewire-0.3
mkdir -p "$out/temp" "$out/stage/DEBIAN" "$out/source/packaging"
export TMPDIR="${SHIELD_BUILD_TMPDIR:-$out/temp}" CARGO_TARGET_DIR="${SHIELD_BUILD_TARGET_DIR:-$out/build}" PYTHONDONTWRITEBYTECODE=1
python3 - "$root" "$TMPDIR" "$CARGO_TARGET_DIR" <<'PY'
from pathlib import Path
import sys
debug = Path(sys.argv[1]).resolve() / 'debug'
for value in sys.argv[2:]:
    path = Path(value).resolve()
    if not path.is_relative_to(debug) or path == debug:
        raise SystemExit('Build and temporary directories must stay under repository debug/.')
    path.mkdir(parents=True, exist_ok=True)
PY
export CARGO_BUILD_JOBS=${CARGO_BUILD_JOBS:-4}
commit=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["commit"])' "$root/platform/linux/upstream.json")
git clone --no-checkout https://github.com/satyrnidae/shieldd.git "$out/upstream"
git -C "$out/upstream" checkout --detach "$commit"
[[ $(git -C "$out/upstream" rev-parse HEAD) == "$commit" ]]
(
    cd "$out/upstream"
    cargo test --workspace --locked --target x86_64-unknown-linux-gnu
    cargo build -p shieldd --release --locked --target x86_64-unknown-linux-gnu
)
python3 "$root/platform/linux/test_launcher.py"
sh -n "$root/platform/linux/postinst"
sh -n "$root/platform/linux/postrm"
binary="$CARGO_TARGET_DIR/x86_64-unknown-linux-gnu/release/shieldd"
"$binary" --help > "$out/shieldd-help.txt"
"$binary" --version > "$out/shieldd-version.txt"
ldd "$binary" > "$out/linked-libraries.txt"
if grep -q 'not found' "$out/linked-libraries.txt"; then echo 'Runtime library missing.' >&2; exit 1; fi
stage="$out/stage"
install -Dm755 "$binary" "$stage/usr/lib/nvidia-shield-p2570/shieldd"
install -Dm755 "$root/platform/linux/nvidia-shield-p2570.py" "$stage/usr/bin/nvidia-shield-p2570"
install -Dm644 "$root/platform/linux/nvidia-shield-p2570.desktop" "$stage/usr/share/applications/nvidia-shield-p2570.desktop"
install -Dm644 "$root/assets/controller.png" "$stage/usr/share/icons/hicolor/256x256/apps/nvidia-shield-p2570.png"
install -Dm644 "$out/upstream/shieldd/res/70-shieldd.rules" "$stage/usr/lib/udev/rules.d/70-nvidia-shield-p2570.rules"
install -Dm644 "$out/upstream/shieldd/res/modules-load.conf" "$stage/usr/lib/modules-load.d/nvidia-shield-p2570.conf"
install -Dm644 "$out/upstream/LICENSE" "$stage/usr/share/doc/nvidia-shield-p2570-linux-preview/copyright"
install -Dm644 "$out/upstream/THIRD_PARTY.md" "$stage/usr/share/doc/nvidia-shield-p2570-linux-preview/UPSTREAM-NOTICES.md"
install -Dm644 "$root/platform/linux/README.md" "$stage/usr/share/doc/nvidia-shield-p2570-linux-preview/README.md"
install -Dm644 "$root/platform/linux/upstream.json" "$stage/usr/share/doc/nvidia-shield-p2570-linux-preview/upstream.json"
install -m755 "$root/platform/linux/postinst" "$stage/DEBIAN/postinst"
install -m755 "$root/platform/linux/postrm" "$stage/DEBIAN/postrm"
# Vendor every locked Rust dependency and include its full source and notices.
mkdir -p "$out/source/shieldd-upstream"
git -C "$out/upstream" archive "$commit" | tar -xf - -C "$out/source/shieldd-upstream"
python3 "$root/platform/linux/source_bundle.py" "$out"
size=$(du -sk "$stage/usr" | cut -f1)
cat > "$stage/DEBIAN/control" <<EOF
Package: nvidia-shield-p2570-linux-preview
Version: ${version}~preview1
Architecture: amd64
Maintainer: NVIDIA SHIELD P2570 community project <noreply@github.com>
Installed-Size: $size
Depends: libc6 (>= 2.39), libgcc-s1, libgtk-4-1 (>= 4.10), libpipewire-0.3-0t64, python3, zenity, udev, kmod
Recommends: libcanberra0
Section: games
Priority: optional
Homepage: https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows
Description: Original SHIELD controller wireless host (Linux preview)
 Native shieldd backend and guided desktop launcher for Ubuntu 24.04 x86_64.
 Requires a dedicated RTL8811AU/RTL8821AU USB adapter with ID 0bda:0811,
 a local desktop session and the uinput kernel module. Built-in Wi-Fi is
 not supported by this preview. Physical hardware validation is pending.
EOF
# Include the exact upstream source and our packaging source alongside the binary.
cp -a "$root/platform/linux/." "$out/source/packaging/"
cp "$root/assets/controller.png" "$out/source/packaging/controller.png"
# Windows checkouts can report every source file as 0777; never ship that mode.
find "$out/source/packaging" -type d -exec chmod 755 {} +
find "$out/source/packaging" -type f -exec chmod 644 {} +
printf '%s\n' "$commit" > "$out/source/UPSTREAM_COMMIT"
printf '%s\n' 'See packaging/README.md for build instructions and known hardware limits.' > "$out/source/README"
deb="$out/NVIDIA-SHIELD-P2570-Linux-Preview-${version}_amd64.deb"
source="$out/NVIDIA-SHIELD-P2570-Linux-Preview-${version}-source.tar.gz"
dpkg-deb --root-owner-group --build "$stage" "$deb"
tar -czf "$source" -C "$out/source" .
dpkg-deb --info "$deb" > "$out/package-info.txt"
dpkg-deb --contents "$deb" > "$out/package-contents.txt"
python3 "$root/platform/linux/test_package.py" "$out"
(
    cd "$out"
    sha256sum "$(basename "$deb")" "$(basename "$source")" > LINUX-SHA256SUMS.txt
)
printf '%s\n' "$deb" "$source"
