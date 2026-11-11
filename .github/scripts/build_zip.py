#!/usr/bin/env python3
"""Build a clean VPM .zip archive from Packages/com.thecocoa101.cocoas-toolbox."""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import tempfile
import zipfile
from pathlib import Path

PACKAGE_REL_DIR = Path("Packages") / "com.thecocoa101.cocoas-toolbox"
SEMVER_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")


def is_ignored_part(name: str) -> bool:
    return name.startswith(".") or name.endswith("~") or name in {"_site", "dist", "__pycache__"}


def make_fallback_meta(rel_posix: str, is_dir: bool) -> str:
    guid = hashlib.md5(f"cocoas-toolbox:{rel_posix}".encode("utf-8")).hexdigest()
    if is_dir:
        return (
            "fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "folderAsset: yes\n"
            "DefaultImporter:\n"
            "  externalObjects: {}\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n"
        )
    if rel_posix.endswith(".cs"):
        return (
            "fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "MonoImporter:\n"
            "  externalObjects: {}\n"
            "  serializedVersion: 2\n"
            "  defaultReferences: []\n"
            "  executionOrder: 0\n"
            "  icon: {instanceID: 0}\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n"
        )
    if rel_posix.endswith(".asmdef"):
        importer = "AssemblyDefinitionImporter"
    elif rel_posix == "package.json":
        importer = "PackageManifestImporter"
    elif rel_posix.endswith((".md", ".txt", ".json", ".uss", ".uxml", ".shader", ".cginc", ".hlsl")):
        importer = "TextScriptImporter"
    else:
        importer = "DefaultImporter"
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        f"{importer}:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def main() -> int:
    repo_root = Path(__file__).resolve().parents[2]
    pkg_dir = repo_root / PACKAGE_REL_DIR
    pkg_json_path = pkg_dir / "package.json"
    if not pkg_json_path.is_file():
        print(f"ERROR: package.json not found at {pkg_json_path}.", file=sys.stderr)
        return 1

    manifest = json.loads(pkg_json_path.read_text(encoding="utf-8"))
    pkg_name = manifest.get("name", "").strip()
    version = manifest.get("version", "").strip()

    if not pkg_name or not version:
        print("ERROR: 'name' and 'version' are required in package.json.", file=sys.stderr)
        return 1
    if not SEMVER_RE.match(version):
        print(f"ERROR: Invalid semantic version '{version}' in package.json.", file=sys.stderr)
        return 1

    gh_repo = os.environ.get("GITHUB_REPOSITORY", "TheCocoa101/CocoasToolbox")
    owner, repo = gh_repo.split("/", 1)
    tag = f"v{version}"
    zip_name = f"{pkg_name}-{version}.zip"
    pages_index_url = f"https://{owner.lower()}.github.io/{repo}/index.json"
    download_url = f"https://github.com/{owner}/{repo}/releases/download/{tag}/{zip_name}"

    manifest["url"] = download_url
    manifest["repo"] = pages_index_url

    out_dir = Path(os.environ.get("RUNNER_TEMP") or tempfile.gettempdir())
    out_dir.mkdir(parents=True, exist_ok=True)
    zip_path = out_dir / zip_name

    included_files: set[str] = set()
    included_dirs: list[str] = []

    for root, dirs, files in os.walk(pkg_dir):
        dirs[:] = sorted(d for d in dirs if not is_ignored_part(d))
        rel_root = Path(root).relative_to(pkg_dir)

        for d in dirs:
            rel_dir = (rel_root / d).as_posix()
            included_dirs.append(rel_dir)

        for f in sorted(files):
            if is_ignored_part(f) or f.endswith(".zip"):
                continue
            rel_file = (rel_root / f).as_posix()
            included_files.add(rel_file)

    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for rel_file in sorted(included_files):
            if rel_file == "package.json":
                data = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
                zf.writestr("package.json", data)
            else:
                zf.write(pkg_dir / rel_file, arcname=rel_file)

            if not rel_file.endswith(".meta"):
                meta_rel = f"{rel_file}.meta"
                if meta_rel not in included_files:
                    print(f"WARNING: Generating fallback .meta in zip for file: {rel_file}")
                    zf.writestr(meta_rel, make_fallback_meta(rel_file, is_dir=False).encode("utf-8"))

        for rel_dir in sorted(included_dirs):
            meta_rel = f"{rel_dir}.meta"
            if meta_rel not in included_files:
                print(f"WARNING: Generating fallback .meta in zip for folder: {rel_dir}")
                zf.writestr(meta_rel, make_fallback_meta(rel_dir, is_dir=True).encode("utf-8"))

    sha256_hex = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    print(f"Built {zip_path} (SHA256: {sha256_hex})")

    is_prerelease = "-" in version
    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output:
        with open(gh_output, "a", encoding="utf-8") as f:
            f.write(f"pkg_name={pkg_name}\n")
            f.write(f"version={version}\n")
            f.write(f"tag={tag}\n")
            f.write(f"zip_name={zip_name}\n")
            f.write(f"zip_path={zip_path}\n")
            f.write(f"zip_sha256={sha256_hex}\n")
            f.write(f"prerelease={'true' if is_prerelease else 'false'}\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
