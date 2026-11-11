#!/usr/bin/env python3
"""Generate VPM index.json, vpm.json, and VCC/ALCOM landing page from GitHub Releases."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import subprocess
import tempfile
import urllib.parse
import zipfile
from pathlib import Path

PACKAGE_REL_DIR = Path("Packages") / "com.thecocoa101.cocoas-toolbox"


def parse_semver_key(ver: str) -> tuple:
    core, _, pre = ver.partition("-")
    core = core.split("+", 1)[0]
    nums = []
    for part in core.split("."):
        try:
            nums.append(int(part))
        except ValueError:
            nums.append(0)
    while len(nums) < 3:
        nums.append(0)
    return (nums[0], nums[1], nums[2], 1 if not pre else 0, pre)


def fetch_releases(gh_repo: str) -> list[dict]:
    cmd = ["gh", "api", f"repos/{gh_repo}/releases", "--paginate"]
    out = subprocess.check_output(cmd, text=True, encoding="utf-8")
    decoder = json.JSONDecoder()
    idx = 0
    releases: list[dict] = []
    out_stripped = out.strip()
    while idx < len(out_stripped):
        obj, next_idx = decoder.raw_decode(out_stripped, idx)
        if isinstance(obj, list):
            releases.extend(obj)
        idx = next_idx
        while idx < len(out_stripped) and out_stripped[idx].isspace():
            idx += 1
    return releases


def build_html(
    display_name: str,
    description: str,
    gh_repo: str,
    index_url: str,
    versions_info: list[dict],
) -> str:
    vcc_url = f"vcc://vpm/addRepo?url={urllib.parse.quote(index_url, safe='')}"
    repo_web_url = f"https://github.com/{gh_repo}"

    rows = []
    for item in versions_info:
        ver = html.escape(item["version"])
        unity_ver = html.escape(item.get("unity", "2022.3"))
        dl_url = html.escape(item["url"])
        is_pre = "-" in item["version"]
        pre_badge = ' <span style="background:#e09f3e;color:#121318;font-size:0.75rem;padding:2px 6px;border-radius:4px;font-weight:700;margin-left:6px;vertical-align:middle;">Pre-release</span>' if is_pre else ''
        rows.append(
            f"<tr><td><code>v{ver}</code>{pre_badge}</td><td>Unity {unity_ver}+</td>"
            f'<td><a href="{dl_url}">.zip ダウンロード</a></td></tr>'
        )
    rows_html = "\n".join(rows) if rows else '<tr><td colspan="3">リリース準備中</td></tr>'

    return f"""<!DOCTYPE html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{html.escape(display_name)} — VPM Repository</title>
  <style>
    :root {{
      color-scheme: dark;
      --bg: #121318;
      --card: #1b1d26;
      --border: #2d3142;
      --accent: #7c9cff;
      --accent-hover: #9bb3ff;
      --text: #ebedf5;
      --muted: #9ca3b8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Hiragino Sans", "Yu Gothic UI", sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.6;
      padding: 40px 20px;
    }}
    .container {{
      max-width: 720px;
      margin: 0 auto;
    }}
    .card {{
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 28px;
      margin-bottom: 20px;
    }}
    h1 {{ margin: 0 0 8px; font-size: 1.8rem; }}
    h2 {{ margin: 0 0 14px; font-size: 1.15rem; }}
    p.desc {{ margin: 0 0 22px; color: var(--muted); }}
    .btn-primary {{
      display: inline-block;
      background: var(--accent);
      color: #0e1017;
      font-weight: 700;
      text-decoration: none;
      padding: 12px 24px;
      border-radius: 10px;
      transition: background 0.15s;
    }}
    .btn-primary:hover {{ background: var(--accent-hover); }}
    .url-box {{
      display: flex;
      gap: 8px;
      margin-top: 10px;
    }}
    .url-box input {{
      flex: 1;
      background: #101218;
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 9px 12px;
      font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
      font-size: 0.9rem;
    }}
    .url-box button {{
      background: #282c3d;
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 0 16px;
      cursor: pointer;
      font-weight: 600;
    }}
    .url-box button:hover {{ background: #34394f; }}
    table {{
      width: 100%;
      border-collapse: collapse;
      font-size: 0.95rem;
    }}
    th, td {{
      text-align: left;
      padding: 10px 8px;
      border-bottom: 1px solid var(--border);
    }}
    th {{ color: var(--muted); font-weight: 600; }}
    a {{ color: var(--accent); }}
    .footer {{ text-align: center; color: var(--muted); font-size: 0.85rem; margin-top: 24px; }}
  </style>
</head>
<body>
  <div class="container">
    <div class="card">
      <h1>{html.escape(display_name)}</h1>
      <p class="desc">{html.escape(description)}</p>
      <a class="btn-primary" href="{vcc_url}">✦ VCC / ALCOM にワンクリックで追加</a>
    </div>

    <div class="card">
      <h2>VPM リポジトリ URL（VCC / ALCOM 手動登録用）</h2>
      <div class="url-box">
        <input id="vpm-url" type="text" readonly value="{html.escape(index_url)}">
        <button onclick="copyField('vpm-url', this)">コピー</button>
      </div>
    </div>

    <div class="card">
      <h2>公開バージョン一覧</h2>
      <table>
        <thead>
          <tr><th>バージョン</th><th>対応 Unity</th><th>アーカイブ</th></tr>
        </thead>
        <tbody>
          {rows_html}
        </tbody>
      </table>
    </div>

    <div class="footer">
      <a href="{html.escape(repo_web_url)}">GitHub Repository ({html.escape(gh_repo)})</a>
    </div>
  </div>
  <script>
    function copyField(id, btn) {{
      const el = document.getElementById(id);
      navigator.clipboard.writeText(el.value);
      const orig = btn.textContent;
      btn.textContent = "コピー完了!";
      setTimeout(() => btn.textContent = orig, 1500);
    }}
  </script>
</body>
</html>
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default="_site", help="Output directory for GitHub Pages")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    root_manifest = json.loads((repo_root / PACKAGE_REL_DIR / "package.json").read_text(encoding="utf-8"))

    gh_repo = os.environ.get("GITHUB_REPOSITORY", "TheCocoa101/CocoasToolbox")
    owner, repo = gh_repo.split("/", 1)
    index_url = f"https://{owner.lower()}.github.io/{repo}/index.json"

    author_obj = root_manifest.get("author", {})
    author_name = author_obj.get("name", owner) if isinstance(author_obj, dict) else str(author_obj)
    display_name = root_manifest.get("displayName", repo)
    description = root_manifest.get("description", "")

    packages: dict[str, dict] = {}
    versions_for_page: list[dict] = []

    releases = fetch_releases(gh_repo)
    with tempfile.TemporaryDirectory() as tmpdir:
        tmp_path = Path(tmpdir)
        for rel in releases:
            if rel.get("draft"):
                continue
            tag_name = rel.get("tag_name", "")
            assets = rel.get("assets", [])
            zip_assets = [a for a in assets if a.get("name", "").endswith(".zip")]
            if not zip_assets:
                continue

            dl_dir = tmp_path / tag_name
            dl_dir.mkdir(parents=True, exist_ok=True)
            subprocess.check_call(
                [
                    "gh",
                    "release",
                    "download",
                    tag_name,
                    "--repo",
                    gh_repo,
                    "--pattern",
                    "*.zip",
                    "--dir",
                    str(dl_dir),
                ]
            )

            for asset in zip_assets:
                zip_file = dl_dir / asset["name"]
                if not zip_file.is_file():
                    continue
                sha256_hex = hashlib.sha256(zip_file.read_bytes()).hexdigest()
                with zipfile.ZipFile(zip_file, "r") as zf:
                    if "package.json" not in zf.namelist():
                        continue
                    pkg_manifest = json.loads(zf.read("package.json").decode("utf-8"))

                p_name = pkg_manifest.get("name")
                p_ver = pkg_manifest.get("version")
                if not p_name or not p_ver:
                    continue

                # 1.0.0 以前の非公開フィルタ（MIN_PUBLIC_VERSION 未満は除外）
                min_ver = os.environ.get("MIN_PUBLIC_VERSION", "1.0.0-0")
                if min_ver and parse_semver_key(p_ver) < parse_semver_key(min_ver):
                    print(f"Skipping v{p_ver} (older than min public version v{min_ver})")
                    continue

                pkg_manifest["url"] = asset["browser_download_url"]
                pkg_manifest["zipSHA256"] = sha256_hex
                pkg_manifest["repo"] = index_url

                packages.setdefault(p_name, {"versions": {}})["versions"][p_ver] = pkg_manifest
                versions_for_page.append(pkg_manifest)

    versions_for_page.sort(key=lambda m: parse_semver_key(m.get("version", "0.0.0")), reverse=True)

    listing = {
        "name": f"{display_name}",
        "id": f"com.{owner.lower()}.{repo.lower()}",
        "url": index_url,
        "author": author_name,
        "packages": packages,
    }

    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    listing_json = json.dumps(listing, ensure_ascii=False, indent=2) + "\n"
    (out_dir / "index.json").write_text(listing_json, encoding="utf-8")
    (out_dir / "vpm.json").write_text(listing_json, encoding="utf-8")
    (out_dir / ".nojekyll").write_text("", encoding="utf-8")
    (out_dir / "index.html").write_text(
        build_html(display_name, description, gh_repo, index_url, versions_for_page),
        encoding="utf-8",
    )

    print(f"Generated VPM listing at {out_dir / 'index.json'} with {len(versions_for_page)} version(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
