#!/usr/bin/env python3
"""
Updates Jellyfin plugin manifest.json with release details, MD5 checksum, and changelog.

Usage:
    python update_manifest.py --zip-path ai-search.zip --version 1.4.0.0 --changelog-file release_summary.txt
"""

import argparse
import datetime
import hashlib
import json
import os
import sys


def compute_md5(file_path: str) -> str:
    hash_md5 = hashlib.md5()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            hash_md5.update(chunk)
    return hash_md5.hexdigest()


def main():
    parser = argparse.ArgumentParser(description="Update Jellyfin manifest.json")
    parser.add_argument("--manifest-path", default="manifest.json", help="Path to manifest.json")
    parser.add_argument("--zip-path", required=True, help="Path to compiled ai-search.zip")
    parser.add_argument("--version", required=True, help="New release version (e.g. 1.4.0.0)")
    parser.add_argument("--changelog-file", help="Path to markdown/text changelog summary")
    parser.add_argument("--repo", default="FaserF/jellyfin-plugin-ai-search", help="GitHub repo (owner/repo)")
    args = parser.parse_args()

    if not os.path.exists(args.zip_path):
        print(f"Error: {args.zip_path} not found", file=sys.stderr)
        sys.exit(1)

    checksum = compute_md5(args.zip_path)
    print(f"Calculated MD5 for {args.zip_path}: {checksum}")

    changelog = ""
    if args.changelog_file and os.path.exists(args.changelog_file):
        with open(args.changelog_file, "r", encoding="utf-8") as f:
            changelog = f.read().strip()
    if not changelog:
        changelog = f"Release {args.version}"

    with open(args.manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    now_iso = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    source_url = f"https://github.com/{args.repo}/releases/download/v{args.version}/ai-search.zip"

    # Supported target ABIs (Jellyfin 12.0 and 10.10)
    target_abis = ["12.0.0.0", "10.10.0.0"]

    for plugin in manifest:
        if plugin.get("guid", "").lower() == "d0a3f1e6-9c2b-4a7d-8e5f-1b2c3d4e5f6a":
            existing_versions = plugin.get("versions", [])
            updated_versions = [v for v in existing_versions if v.get("version") != args.version]

            for abi in target_abis:
                updated_versions.insert(0, {
                    "version": args.version,
                    "changelog": changelog,
                    "targetAbi": abi,
                    "sourceUrl": source_url,
                    "checksum": checksum,
                    "timestamp": now_iso
                })

            plugin["versions"] = updated_versions
            plugin["owner"] = "FaserF"

    with open(args.manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Successfully updated {args.manifest_path} for version {args.version}")


if __name__ == "__main__":
    main()
