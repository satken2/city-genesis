#!/usr/bin/env python3
"""
コアの OpenAPI 仕様を取得し、共有資料 api/openapi.json を更新する。
引数 --source は URL またはローカルファイルで、省略時は起動中のコアと Unity の仕様を順に試す。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
DEST_JSON = REPO_ROOT / "api" / "openapi.json"
DEFAULT_SOURCE = "http://localhost:5000/openapi.json"
FALLBACK_PATH = REPO_ROOT.parent / "unity" / "openapi.json"


def fetch_from_url(url: str) -> dict:
    import urllib.request
    with urllib.request.urlopen(url) as resp:
        return json.loads(resp.read())


def fetch_from_file(path: str) -> dict:
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"File not found: {p}")
    return json.loads(p.read_text(encoding="utf-8"))


def main():
    parser = argparse.ArgumentParser(description="Fetch the shared OpenAPI specification")
    parser.add_argument("--source", default=None,
                        help="URL or local file path. Default: try localhost:5000, then fallback")
    args = parser.parse_args()

    spec = None

    if args.source:
        if args.source.startswith("http"):
            spec = fetch_from_url(args.source)
        else:
            spec = fetch_from_file(args.source)
    else:
        try:
            spec = fetch_from_url(DEFAULT_SOURCE)
            print(f"Fetched from {DEFAULT_SOURCE}")
        except Exception:
            print(f"Could not reach {DEFAULT_SOURCE}, trying fallback: {FALLBACK_PATH}")
            if FALLBACK_PATH.exists():
                spec = fetch_from_file(str(FALLBACK_PATH))
                print(f"Fetched from {FALLBACK_PATH}")
            else:
                print(f"Error: fallback not found at {FALLBACK_PATH}", file=sys.stderr)
                sys.exit(1)

    DEST_JSON.parent.mkdir(parents=True, exist_ok=True)

    DEST_JSON.write_text(json.dumps(spec, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Saved JSON to {DEST_JSON} ({DEST_JSON.stat().st_size / 1024:.1f} KB)")

if __name__ == "__main__":
    main()
