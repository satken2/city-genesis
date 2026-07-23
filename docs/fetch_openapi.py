#!/usr/bin/env python3
"""
Usage: python fetch_openapi.py [--source URL_OR_PATH]

Fetches the OpenAPI spec from the running FastAPI backend (default: http://localhost:5000)
and generates docs/api/index.html with the spec inlined (Redoc).
Also saves docs/api/openapi.json as a standalone file.
"""

import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
DEST_JSON = REPO_ROOT / "docs" / "api" / "openapi.json"
DEST_HTML = REPO_ROOT / "docs" / "api" / "index.html"
TEMPLATE_HTML = REPO_ROOT / "docs" / "api" / "_template.html"
DEFAULT_SOURCE = "http://localhost:5000/openapi.json"
FALLBACK_PATH = REPO_ROOT.parent / "unity" / "openapi.json"
PLACEHOLDER = "/* __OPENAPI_SPEC_PLACEHOLDER__ */"


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
    parser = argparse.ArgumentParser(description="Fetch OpenAPI spec and generate Redoc page")
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

    html = TEMPLATE_HTML.read_text(encoding="utf-8")
    html = html.replace(PLACEHOLDER, json.dumps(spec, ensure_ascii=False))
    DEST_HTML.write_text(html, encoding="utf-8")
    print(f"Saved HTML to {DEST_HTML} ({DEST_HTML.stat().st_size / 1024:.1f} KB)")


if __name__ == "__main__":
    main()
