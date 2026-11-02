"""Bundle corresponding Rust dependency source and notices for the Linux binary."""
import json
import hashlib
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
supplement_dir = Path(__file__).resolve().parent / "license-notices"
supplements = {(item["package"], item["version"]): item for item in json.loads((supplement_dir / "manifest.json").read_text())}
used_supplements = []
for package in sorted(metadata["packages"], key=lambda item: (item["name"], item["version"])):
    folder = Path(package["manifest_path"]).parent
    copied = []
    candidates = [entry for entry in folder.iterdir() if entry.is_file() and entry.name.upper().startswith(("LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "NOTICE", "UNLICENSE"))]
    if package.get("license_file"):
        explicit = folder / package["license_file"]
        if explicit.is_file() and explicit not in candidates:
            candidates.append(explicit)
    if not candidates and folder.is_relative_to(source) and not folder.is_relative_to(vendor):
        # The pinned workspace crates inherit the GPL license from its root.
        candidates.append(source / "LICENSE")
    if not candidates and package["name"] == "r-efi":
        # This upstream stores its full MIT grant and copyright in AUTHORS.
        authors = folder / "AUTHORS"
        if authors.is_file() and "Permission is hereby granted" in authors.read_text():
            candidates.append(authors)
    if not candidates and (package["name"], package["version"]) in supplements:
        supplement = supplements[(package["name"], package["version"])]
        notice = supplement_dir / supplement["file"]
        if hashlib.sha256(notice.read_bytes()).hexdigest() != supplement["sha256"]:
            raise SystemExit(f"Supplemental license checksum mismatch: {notice.name}")
        vcs = folder / ".cargo_vcs_info.json"
        if vcs.is_file() and json.loads(vcs.read_text())["git"]["sha1"] != supplement["commit"]:
            raise SystemExit(f"Supplemental license provenance mismatch: {package['name']}")
        candidates.append(notice)
        used_supplements.append(supplement)
    if not candidates:
        raise SystemExit(f"Missing redistribution notice: {package['name']} {package['version']}")
    for candidate in candidates:
        target = notices / (package["name"] + "-" + package["version"]) / candidate.name
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(candidate, target)
        copied.append(str(target.relative_to(stage)))
    inventory.append({"name": package["name"], "version": package["version"], "license": package.get("license"),
                      "repository": package.get("repository"), "notices": sorted(copied), "source_in_archive": True})
(stage / "THIRD-PARTY-CRATES.json").write_text(json.dumps(inventory, indent=2) + "\n")
(out / "source/dependency-inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
(stage / "SUPPLEMENTAL-NOTICE-SOURCES.json").write_text(json.dumps(used_supplements, indent=2) + "\n")
print(json.dumps({"corresponding_source": True, "packages": len(inventory), "notice_files": sum(len(item["notices"]) for item in inventory)}))
