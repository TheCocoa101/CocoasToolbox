#!/usr/bin/env python3
"""Build a clean VPM .zip archive from a package under Packages/com.thecocoa101.*."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
import zipfile
from pathlib import Path

SEMVER_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")


def is_ignored_part(name: str) -> bool:
    return name.startswith(".") or name.endswith("~") or name in {"_site", "dist", "__pycache__"}


def make_fallback_meta(pkg_name: str, rel_posix: str, is_dir: bool) -> str:
    guid = hashlib.md5(f"{pkg_name}:{rel_posix}".encode("utf-8")).hexdigest()
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


def resolve_package_dir(repo_root: Path, pkg_arg: str) -> Path:
    candidate = Path(pkg_arg)
    if candidate.is_dir():
        return candidate.resolve()
    rel_candidate = repo_root / pkg_arg
    if rel_candidate.is_dir():
        return rel_candidate.resolve()
    pkg_candidate = repo_root / "Packages" / pkg_arg
    if pkg_candidate.is_dir():
        return pkg_candidate.resolve()
    raise FileNotFoundError(f"Package directory not found for '{pkg_arg}'.")


def build_package_zip(pkg_dir: Path, out_dir: Path, log_stream=sys.stdout) -> dict:
    pkg_json_path = pkg_dir / "package.json"
    if not pkg_json_path.is_file():
        raise FileNotFoundError(f"package.json not found at {pkg_json_path}.")

    manifest = json.loads(pkg_json_path.read_text(encoding="utf-8"))
    pkg_name = manifest.get("name", "").strip()
    display_name = manifest.get("displayName", pkg_name).strip()
    version = manifest.get("version", "").strip()

    if not pkg_name or not version:
        raise ValueError(f"'name' and 'version' are required in {pkg_json_path}.")
    if not SEMVER_RE.match(version):
        raise ValueError(f"Invalid semantic version '{version}' in {pkg_json_path}.")

    gh_repo = os.environ.get("GITHUB_REPOSITORY", "TheCocoa101/CocoasToolbox")
    owner, repo = gh_repo.split("/", 1)
    tag = f"{pkg_name}-v{version}"
    zip_name = f"{pkg_name}-{version}.zip"
    pages_index_url = f"https://{owner.lower()}.github.io/{repo}/index.json"
    download_url = f"https://github.com/{owner}/{repo}/releases/download/{tag}/{zip_name}"

    manifest["url"] = download_url
    manifest["repo"] = pages_index_url

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
                    print(f"WARNING: Generating fallback .meta in zip for file: {rel_file}", file=log_stream)
                    zf.writestr(meta_rel, make_fallback_meta(pkg_name, rel_file, is_dir=False).encode("utf-8"))

        for rel_dir in sorted(included_dirs):
            meta_rel = f"{rel_dir}.meta"
            if meta_rel not in included_files:
                print(f"WARNING: Generating fallback .meta in zip for folder: {rel_dir}", file=log_stream)
                zf.writestr(meta_rel, make_fallback_meta(pkg_name, rel_dir, is_dir=True).encode("utf-8"))

    sha256_hex = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    print(f"Built {zip_path} (SHA256: {sha256_hex})", file=log_stream)

    return {
        "pkg_name": pkg_name,
        "display_name": display_name,
        "version": version,
        "tag": tag,
        "zip_name": zip_name,
        "zip_path": str(zip_path),
        "zip_sha256": sha256_hex,
        "prerelease": "-" in version,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, help="Package directory or package name under Packages/")
    parser.add_argument("--out-dir", default="", help="Output directory for zip (defaults to RUNNER_TEMP or temp dir)")
    parser.add_argument("--json", action="store_true", help="Print build result metadata as JSON to stdout")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    try:
        pkg_dir = resolve_package_dir(repo_root, args.package)
        out_dir = Path(args.out_dir) if args.out_dir else Path(os.environ.get("RUNNER_TEMP") or tempfile.gettempdir())
        result = build_package_zip(pkg_dir, out_dir, log_stream=sys.stderr if args.json else sys.stdout)
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, ensure_ascii=False))

    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output and not args.json:
        with open(gh_output, "a", encoding="utf-8") as f:
            f.write(f"pkg_name={result['pkg_name']}\n")
            f.write(f"display_name={result['display_name']}\n")
            f.write(f"version={result['version']}\n")
            f.write(f"tag={result['tag']}\n")
            f.write(f"zip_name={result['zip_name']}\n")
            f.write(f"zip_path={result['zip_path']}\n")
            f.write(f"zip_sha256={result['zip_sha256']}\n")
            f.write(f"prerelease={'true' if result['prerelease'] else 'false'}\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
