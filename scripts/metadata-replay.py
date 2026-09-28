#!/usr/bin/env python3
"""Record/replay proxy for the metadata services Wondarr calls (Phase 1 gate).

The app is pointed at this proxy through its `metadata.*_base_url` settings:

    APP__METADATA__MUSICBRAINZ_BASE_URL=http://HOST:PORT/mb/ws/2/
    APP__METADATA__COVER_ART_ARCHIVE_BASE_URL=http://HOST:PORT/caa/
    APP__METADATA__DEEZER_BASE_URL=http://HOST:PORT/deezer/
    APP__METADATA__ITUNES_BASE_URL=http://HOST:PORT/itunes/

`--mode record` forwards every request to the real service (spacing requests per host so the
MusicBrainz 1 req/s rule holds even if the app misbehaves) and stores the answer; `--mode replay`
answers from the stored files only, so CI proves the gate without touching the real services.
A request with no recording gets the service's own "not found" shape and is listed at /__misses.

Standard library only. Usage:
    python3 scripts/metadata-replay.py --mode replay --dir tests/gate/replay --port 18099
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

UPSTREAMS = {
    "mb": ("https://musicbrainz.org", 1.1),
    "caa": ("https://coverartarchive.org", 0.25),
    "deezer": ("https://api.deezer.com", 0.15),
    "itunes": ("https://itunes.apple.com", 3.1),
}

# Headers worth keeping from a recorded answer; everything else is noise for the app.
KEPT_HEADERS = ("content-type", "location", "retry-after")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Cover Art Archive answers 307 with a Location; the app wants to see that, not the image."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D102 - stdlib hook
        return None


OPENER = urllib.request.build_opener(NoRedirect)


def not_found(service: str, path: str) -> tuple[int, dict[str, str], bytes]:
    """The answer each service gives for something it does not know."""
    json_type = {"content-type": "application/json; charset=utf-8"}
    if service == "mb":
        return 404, json_type, b'{"help":"replay","error":"Not Found"}'
    if service == "deezer":
        if path.startswith("/search"):
            return 200, json_type, b'{"data":[],"total":0}'
        return 200, json_type, b'{"error":{"type":"DataException","message":"no data","code":800}}'
    if service == "itunes":
        return 200, json_type, b'{"resultCount":0,"results":[]}'
    return 404, {"content-type": "text/plain"}, b"not found"


class Store:
    def __init__(self, root: Path, mode: str) -> None:
        self.root = root
        self.mode = mode
        self.misses: list[str] = []
        self.lock = threading.Lock()
        self.next_slot = {name: 0.0 for name in UPSTREAMS}

    @staticmethod
    def key(method: str, service: str, path_and_query: str) -> str:
        return hashlib.sha1(f"{method} {service}{path_and_query}".encode("utf-8")).hexdigest()[:20]

    def file_for(self, method: str, service: str, path_and_query: str) -> Path:
        # gzip: MusicBrainz release browses are large and repetitive (18 MB of JSON, 2.8 MB gzipped).
        return self.root / service / f"{self.key(method, service, path_and_query)}.json.gz"

    def wait_turn(self, service: str) -> None:
        interval = UPSTREAMS[service][1]
        with self.lock:
            now = time.monotonic()
            slot = max(now, self.next_slot[service])
            self.next_slot[service] = slot + interval
        if slot > now:
            time.sleep(slot - now)

    def fetch(self, method: str, service: str, path_and_query: str, user_agent: str | None):
        target = self.file_for(method, service, path_and_query)
        if target.exists():
            saved = json.loads(gzip.decompress(target.read_bytes()).decode("utf-8"))
            return saved["status"], saved["headers"], saved["body"].encode("utf-8")

        if self.mode == "replay":
            with self.lock:
                self.misses.append(f"{method} /{service}{path_and_query}")
            return not_found(service, path_and_query)

        self.wait_turn(service)
        request = urllib.request.Request(UPSTREAMS[service][0] + path_and_query, method=method)
        request.add_header("User-Agent", user_agent or "Wondarr-gate-recorder ( https://github.com/wulfftech/wondarr )")
        request.add_header("Accept", "application/json")
        try:
            with OPENER.open(request, timeout=30) as response:
                status, headers, body = response.status, dict(response.headers), response.read()
        except urllib.error.HTTPError as error:  # 3xx (no redirect), 4xx, 5xx all land here
            status, headers, body = error.code, dict(error.headers), error.read()

        kept = {k.lower(): v for k, v in headers.items() if k.lower() in KEPT_HEADERS}
        if status in (429, 503) or status >= 500:
            return status, kept, body  # never record throttling or outages
        target.parent.mkdir(parents=True, exist_ok=True)
        record = json.dumps(
            {"request": f"{method} /{service}{path_and_query}", "status": status, "headers": kept,
             "body": body.decode("utf-8", errors="replace")},
            ensure_ascii=False, indent=1,
        ) + "\n"
        # mtime=0 keeps the file byte-identical when the same answer is recorded again.
        target.write_bytes(gzip.compress(record.encode("utf-8"), mtime=0))
        return status, kept, body


def make_handler(store: Store):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):  # quiet: misses are the interesting part
            pass

        def _serve(self, method: str) -> None:
            if self.path == "/__misses":
                body = json.dumps(store.misses, indent=1).encode("utf-8")
                self._reply(200, {"content-type": "application/json"}, body, method)
                return
            if self.path == "/__health":
                self._reply(200, {"content-type": "text/plain"}, b"ok", method)
                return
            service, _, rest = self.path.lstrip("/").partition("/")
            if service not in UPSTREAMS:
                self._reply(404, {"content-type": "text/plain"}, b"unknown service", method)
                return
            status, headers, body = store.fetch(method, service, "/" + rest, self.headers.get("User-Agent"))
            self._reply(status, headers, body, method)

        def _reply(self, status: int, headers: dict[str, str], body: bytes, method: str) -> None:
            self.send_response(status)
            for name, value in headers.items():
                self.send_header(name, value)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if method != "HEAD":
                self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802 - stdlib naming
            self._serve("GET")

        def do_HEAD(self) -> None:  # noqa: N802 - stdlib naming
            self._serve("HEAD")

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mode", choices=("record", "replay"), required=True)
    parser.add_argument("--dir", type=Path, required=True)
    parser.add_argument("--port", type=int, default=18099)
    parser.add_argument("--bind", default="0.0.0.0")
    args = parser.parse_args()

    store = Store(args.dir, args.mode)
    server = ThreadingHTTPServer((args.bind, args.port), make_handler(store))
    print(f"metadata-replay: {args.mode} {args.dir} on {args.bind}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
