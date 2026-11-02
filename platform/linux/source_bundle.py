"""Bundle corresponding Rust dependency source and notices for the Linux binary."""
import json
from pathlib import Path
import shutil
import subprocess
import sys

out = Path(sys.argv[1]).resolve()
if "debug" not in out.parts:
    raise SystemExit("Source-bundle output must be under debug/.")
source = out / "source/shieldd-upstream"
stage = out / "stage/usr/share/doc/nvidia-shield-p2570-linux-preview"
shutil.copyfile(Path(__file__).resolve().parents[2] / "LICENSE", out / "source/LICENSE")
vendor = source / "vendor-cargo"
if not (source / "Cargo.lock").is_file() or not stage.is_dir() or vendor.exists():
    raise SystemExit("Expected a fresh package source/stage tree.")
config = subprocess.check_output(["cargo", "vendor", "--locked", str(vendor)], cwd=source, text=True)
config = config.replace(str(vendor), "vendor-cargo")
with (source / ".cargo/config.toml").open("a") as handle:
    handle.write("\n# Exact dependency sources shipped with this release.\n" + config)
metadata = json.loads(subprocess.check_output(["cargo", "metadata", "--locked", "--offline", "--format-version", "1"], cwd=source, text=True))
notices = stage / "crate-notices"
notices.mkdir()
inventory = []
for package in sorted(metadata["packages"], key=lambda item: (item["name"], item["version"])):
    folder = Path(package["manifest_path"]).parent
    copied = []
    candidates = [entry for entry in folder.iterdir() if entry.is_file() and entry.name.upper().startswith(("LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "NOTICE"))]
    if package.get("license_file"):
        explicit = folder / package["license_file"]
        if explicit.is_file() and explicit not in candidates:
            candidates.append(explicit)
    for candidate in candidates:
        target = notices / (package["name"] + "-" + package["version"]) / candidate.name
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(candidate, target)
        copied.append(str(target.relative_to(stage)))
    inventory.append({"name": package["name"], "version": package["version"], "license": package.get("license"),
                      "repository": package.get("repository"), "notices": sorted(copied), "source_in_archive": True})
(stage / "THIRD-PARTY-CRATES.json").write_text(json.dumps(inventory, indent=2) + "\n")
(out / "source/dependency-inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
print(json.dumps({"corresponding_source": True, "packages": len(inventory), "notice_files": sum(len(item["notices"]) for item in inventory)}))
