#!/usr/bin/env python3
"""
Usage: python fetch_openapi.py [--source URL_OR_PATH]

Fetches the OpenAPI spec from the running FastAPI backend (default: http://localhost:5000)
and generates docs/api/index.html with the spec inlined (Redoc).
Also saves docs/api/openapi.json as a standalone file.

URL からの取得には、ローカルAPIの認証トークン(環境変数 GENESIS_API_TOKEN →
<ユーザーフォルダ>/api_token)を X-City-Genesis-Token として付ける。トークンを他のホストへ
漏らさないよう、付けるのはループバック(127.0.0.1 / localhost / [::1])の URL だけ。
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import sys
from pathlib import Path
from urllib.parse import urlsplit

REPO_ROOT = Path(__file__).resolve().parent
DEST_JSON = REPO_ROOT / "docs" / "api" / "openapi.json"
DEST_HTML = REPO_ROOT / "docs" / "api" / "index.html"
TEMPLATE_HTML = REPO_ROOT / "docs" / "api" / "_template.html"
DEFAULT_SOURCE = "http://localhost:5000/openapi.json"
FALLBACK_PATH = REPO_ROOT.parent / "unity" / "openapi.json"
PLACEHOLDER = "/* __OPENAPI_SPEC_PLACEHOLDER__ */"


TOKEN_HEADER = "X-City-Genesis-Token"
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}


def user_data_dir() -> Path:
    """コアの consts._get_appdata_dir() と同じ <ユーザーフォルダ>。バックエンドはここへ api_token を書く。"""
    system = platform.system()
    if system == "Windows":
        base = os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA") or os.path.expanduser("~/AppData/Local")
    elif system == "Darwin":
        base = os.path.expanduser("~/Library/Application Support")
    else:
        base = os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share")
    return Path(base) / "UrbanGenesis"


def api_token() -> str | None:
    """環境変数 GENESIS_API_TOKEN → トークンファイルの順で読む。バックエンドの起動ごとに変わるので毎回読み直す。"""
    token = os.environ.get("GENESIS_API_TOKEN", "").strip()
    if token:
        return token
    try:
        return (user_data_dir() / "api_token").read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def request_headers(url: str) -> dict[str, str]:
    """ループバックの URL にだけ Host とトークンを付ける。Host はバックエンドの Host 検査を通る形(ホスト名:ポート)で明示する。"""
    parts = urlsplit(url)
    if parts.hostname not in LOOPBACK_HOSTS:
        return {}
    host = f"[{parts.hostname}]" if ":" in parts.hostname else parts.hostname
    headers = {"Host": f"{host}:{parts.port}" if parts.port else host}
    token = api_token()
    if token:
        headers[TOKEN_HEADER] = token
    return headers


def fetch_from_url(url: str) -> dict:
    import urllib.request
    request = urllib.request.Request(url, headers=request_headers(url))
    with urllib.request.urlopen(request) as resp:
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
