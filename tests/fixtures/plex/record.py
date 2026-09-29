"""Records the Plex responses the Plex client is tested against (dev-only; the outputs are committed).

Run from the repo root: `python tests/fixtures/plex/record.py`. It reads PLEX_TOKEN from `.env` with the
same loader as `scripts/worker.py` and only issues read requests against the server (plus creating one
unclaimed sign-in PIN on plex.tv, which expires on its own). Everything that identifies the account,
the server or the household is replaced before writing: tokens, client and machine identifiers, PIN
codes, addresses, names, and library paths.
"""
from __future__ import annotations

import datetime as dt
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "scripts"))
import worker  # noqa: E402  (the .env loader with the inline-comment rule)

OUT = Path(__file__).resolve().parent
CLIENT_ID = "wondarr-fixture-recorder"
HEADERS = {
    "Accept": "application/json",
    "X-Plex-Product": "Wondarr",
    "X-Plex-Version": "0.0.0-dev",
    "X-Plex-Client-Identifier": CLIENT_ID,
}

# Longest first, so a whole value is replaced before any part of it.
REPLACEMENTS: list[tuple[str, str]] = []


def scrub(text: str) -> str:
    for secret, stand_in in sorted(REPLACEMENTS, key=lambda pair: -len(pair[0])):
        if secret:
            text = text.replace(secret, stand_in)
    # Any address or plex.direct host name that slipped through (plex.direct names spell the address with dashes).
    text = re.sub(r"\b\d{1,3}-\d{1,3}-\d{1,3}-\d{1,3}\.[0-9a-f]{32}\.plex\.direct",
                  "192-0-2-10.0123456789abcdef0123456789abcdef.plex.direct", text)
    text = re.sub(r"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])", "192.0.2.10", text)
    return text


def request(method: str, url: str, token: str | None) -> tuple[int, str]:
    headers = dict(HEADERS)
    if token:
        headers["X-Plex-Token"] = token
    req = urllib.request.Request(url, method=method, headers=headers, data=b"" if method in ("POST", "PUT") else None)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")


def save(name: str, method: str, url: str, status: int, body: str) -> None:
    parsed = urllib.parse.urlsplit(url)
    shown = urllib.parse.urlunsplit(("https" if "plex.tv" in parsed.netloc else "http",
                                     parsed.netloc if "plex.tv" in parsed.netloc else "plex.example:32400",
                                     parsed.path, parsed.query, ""))
    try:
        body = json.dumps(json.loads(body), indent=2, ensure_ascii=False) + "\n"
    except ValueError:
        pass
    (OUT / f"{name}.json").write_text(scrub(body), encoding="utf-8", newline="\n")
    meta = {"request": f"{method} {scrub(shown)}", "status": status,
            "captured": dt.date.today().isoformat(), "note": "anonymised by tests/fixtures/plex/record.py"}
    (OUT / f"{name}.meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{name}: {status}")


def anonymise_pin(pin: dict) -> dict:
    """plex.tv geolocates the caller's address into the PIN; none of that belongs in a fixture."""
    if isinstance(pin.get("location"), dict):
        pin["location"] = {**pin["location"], "code": "US", "european_union_member": False, "continent_code": "NA",
                           "country": "United States", "city": "Example City", "time_zone": "Etc/UTC",
                           "postal_code": "00000", "subdivisions": "Example State", "coordinates": "0, 0"}
    return pin


def main() -> None:
    worker.load_env_files()
    token = worker.env("PLEX_TOKEN")
    if not token:
        raise SystemExit("PLEX_TOKEN is not set")
    REPLACEMENTS.append((token, "PLEX-USER-TOKEN"))
    REPLACEMENTS.append((CLIENT_ID, "wondarr-test-client"))

    # --- plex.tv: servers for this account ------------------------------------------------------
    url = "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1"
    status, body = request("GET", url, token)
    resources = json.loads(body)
    servers = [r for r in resources if "server" in (r.get("provides") or "")]
    if not servers:
        raise SystemExit("no Plex Media Server on this account")
    server = servers[0]
    for index, resource in enumerate(resources):
        REPLACEMENTS.append((resource.get("accessToken") or "", f"PLEX-RESOURCE-TOKEN-{index}"))
        REPLACEMENTS.append((resource.get("clientIdentifier") or "", f"{index:040x}"))
        REPLACEMENTS.append((resource.get("name") or "", f"Test Server {index}" if "server" in (resource.get("provides") or "") else f"Test Device {index}"))
        REPLACEMENTS.append((resource.get("device") or "", "Linux"))
        REPLACEMENTS.append((resource.get("publicAddress") or "", "198.51.100.7"))
        for connection in resource.get("connections") or []:
            host = urllib.parse.urlsplit(connection.get("uri") or "").hostname or ""
            if host and not re.fullmatch(r"[\d.]+", host) and "plex.direct" not in host:
                REPLACEMENTS.append((host, "plex.example"))
    # the recordings keep at most the first server and two other resources
    kept = [server] + [r for r in resources if r is not server][:2]
    save("resources", "GET", url, status, json.dumps(kept))

    # --- plex.tv: the PIN flow's first two calls (no sign-in happens) ----------------------------
    url = "https://plex.tv/api/v2/pins?strong=true"
    status, body = request("POST", url, None)
    pin = json.loads(body)
    body = json.dumps(anonymise_pin(pin))
    REPLACEMENTS.append((str(pin.get("code") or ""), "abcdefghijklmnopqrstuvwxy"))
    REPLACEMENTS.append((str(pin.get("id") or ""), "1234567890"))
    save("pin-created", "POST", url, status, body)
    url = f"https://plex.tv/api/v2/pins/{pin['id']}"
    status, body = request("GET", url, None)
    save("pin-pending", "GET", url, status, json.dumps(anonymise_pin(json.loads(body))))

    # --- the server: identity, sections, one music track ----------------------------------------
    base = next((c["uri"] for c in server.get("connections", []) if not c.get("relay") and c.get("local")),
                server["connections"][0]["uri"])
    server_token = server.get("accessToken") or token
    status, body = request("GET", base + "/identity", server_token)
    identity = json.loads(body).get("MediaContainer", {})
    REPLACEMENTS.append((identity.get("machineIdentifier") or "", "0" * 40))
    save("identity", "GET", base + "/identity", status, body)

    status, body = request("GET", base + "/library/sections", server_token)
    sections = json.loads(body)["MediaContainer"].get("Directory", [])
    for index, section in enumerate(sections):
        REPLACEMENTS.append((section.get("uuid") or "", f"00000000-0000-4000-8000-{index:012d}"))
        REPLACEMENTS.append((section.get("title") or "", f"{section.get('type', 'library').title()} {index}"))
        for location in section.get("Location", []):
            REPLACEMENTS.append((location.get("path") or "", f"/data/{section.get('type', 'library')}{index}"))
    save("sections", "GET", base + "/library/sections", status, body)

    music = next((s for s in sections if s.get("type") == "artist"), None)
    if music is None:
        print("no music section; skipping the track recordings")
        return
    url = f"{base}/library/sections/{music['key']}/all?type=10&X-Plex-Container-Start=0&X-Plex-Container-Size=1"
    status, body = request("GET", url, server_token)
    tracks = json.loads(body)["MediaContainer"].get("Metadata", [])
    save("section-tracks", "GET", url, status, body)
    if tracks:
        url = f"{base}/library/metadata/{tracks[0]['ratingKey']}"
        status, body = request("GET", url, server_token)
        save("track-metadata", "GET", url, status, body)

    status, body = request("GET", base + "/activities", server_token)
    save("activities", "GET", base + "/activities", status, body)


if __name__ == "__main__":
    main()
