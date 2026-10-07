#!/usr/bin/env bash
# Phase gate smoke test (docs/build/PHASES.md): runs an image and checks
# Phase 0:
#   - the container starts on 1077 and turns healthy
#   - the UI loads behind a URL base
#   - the API key works (and is required)
#   - health shows DB, folders and the bundled slskd process OK (logged out, no credentials)
#   - a scheduled no-op job's last run survives a restart
#   - files the app creates carry PUID/PGID, and the bundled tools run
# Phase 1 (scripts/phase1-gate.py):
#   - a pasted list of 50 songs resolves >= 90 % to MusicBrainz recordings with correct durations
#     and cover art, the rest via Deezer or into the unresolved review, every song with an album
# Phase 2 (scripts/phase2-gate.py with tools/FakeSlskd; SMOKE_PHASE2=fake (default) | off):
#   - the bundled slskd is replaced by FakeSlskd (a scenario built from the Phase 1 songs, real
#     encoded audio, an AcoustID stand-in); MissingSearch imports >= 80 % of the wanted songs at or
#     above cutoff, a live take disguised as the best file is caught after download and the next
#     candidate imported, a rejected transfer falls through to the next candidate, the Soulseek
#     search budget holds, and toggling "Share my library" changes what slskd shares
# Phase 3 (scripts/phase3-gate.py; SMOKE_PHASE3=on (default) | off), on the same container:
#   - a reference folder of encoded audio (MBID-tagged, text-tagged, untagged, duplicates) is added in
#     adopt mode and scanned: >= 90 % identified automatically, the rest resolved through the Match
#     queue, every new song adopted into the library, the originals byte-for-byte unchanged
# Phase 6 (scripts/phase6-gate.py; SMOKE_PHASE6=on | off (default until its metadata is recorded)), on
#   a fresh container: a 200-track Exportify CSV resolves >= 95 % via ISRC, two search cycles download
#   20 of its songs, a Plex playlist (FakeSlskd's fake Plex) holds them in order and is updated in
#   place after a re-upload, an album added by search arrives pinned, a song moves to a second library
#   (and Plex section), a FLAC is imported as an MP3 still ranked FLAC, and an MP3 is converted on demand.
# Phase 4 (scripts/phase4-gate.py; SMOKE_PHASE4=on (default) | off), on the same container:
#   - the YouTube source is enabled (videos allowed) against the fake InnerTube (answering the gate's
#     songs from the scenario, everything else from the recorded fixtures) and the fake yt-dlp
#     (FakeSlskd --fake-ytdlp)
#     (a stand-in yt-dlp producing real encoded Opus); songs missing on Soulseek are filled from
#     Art Tracks within one search cycle and pass fingerprint verification, an official-video
#     candidate with an intro is rejected by duration, and a simulated bot-check response backs
#     off instead of looping (exactly one grab per run)
#
# Metadata services for the Phase 1 gate (SMOKE_METADATA):
#   replay (default) - scripts/metadata-replay.py answers from tests/gate/replay; no network needed,
#                      and any request without a recording fails the run (re-record then)
#   record           - the proxy forwards to the real services and stores the answers
#   live             - the app talks to the real services directly
#   off              - skip the Phase 1 gate
#
# usage: scripts/smoke-test.sh <image> [docker command, e.g. "sudo docker"]
# needs: bash, curl, jq, python3
set -euo pipefail

IMAGE="${1:?image}"
DOCKER="${2:-docker}"
NAME="wondarr-smoke-$$"
PORT="${SMOKE_PORT:-1077}"
URL_BASE="/wondarr"
PUID_WANT="${SMOKE_PUID:-1234}"
PGID_WANT="${SMOKE_PGID:-2345}"
WORK="$(mktemp -d)"
BASE="http://localhost:${PORT}${URL_BASE}"
METADATA="${SMOKE_METADATA:-replay}"
REPLAY_PORT="${SMOKE_REPLAY_PORT:-18099}"
REPLAY_DIR="${SMOKE_REPLAY_DIR:-$(cd "$(dirname "$0")/.." && pwd)/tests/gate/replay}"
REPLAY_PID=""

cleanup() {
    if [ -n "$REPLAY_PID" ]; then kill "$REPLAY_PID" 2> /dev/null || true; fi
    $DOCKER logs "$NAME" > "$WORK/container.log" 2>&1 || true
    $DOCKER rm -f "$NAME" "${NAME}-fresh" "${NAME}-ext" "${NAME}-extfake" "${NAME}-p6" > /dev/null 2>&1 || true
    if [ "${KEEP_WORK:-0}" != 1 ]; then
        # files belong to PUID/PGID; remove them from inside a container to avoid needing root here
        $DOCKER run --rm -v "$WORK:/w" --entrypoint /bin/sh "$IMAGE" -c 'rm -rf /w/config /w/data /w/fresh /w/ext' > /dev/null 2>&1 || true
        rm -rf "$WORK" 2> /dev/null || true
    fi
}
fail() {
    echo "FAIL: $*" >&2
    $DOCKER logs --tail 80 "$NAME" >&2 || true
    exit 1
}
pass() { echo "ok   $*"; }
trap cleanup EXIT

wait_healthy() {
    local status=""
    for _ in $(seq 1 90); do
        status="$($DOCKER inspect -f '{{.State.Health.Status}}' "$NAME" 2> /dev/null || echo missing)"
        [ "$status" = healthy ] && return 0
        [ "$status" = unhealthy ] && fail "container unhealthy"
        sleep 2
    done
    fail "container not healthy after 180 s (status: $status)"
}

mkdir -p "$WORK/config" "$WORK/data"
chmod 777 "$WORK/config" "$WORK/data"

METADATA_ARGS=()
case "$METADATA" in
    replay | record)
        python3 "$(dirname "$0")/metadata-replay.py" --mode "$METADATA" --dir "$REPLAY_DIR" --port "$REPLAY_PORT" \
            > "$WORK/replay.log" 2>&1 &
        REPLAY_PID=$!
        for _ in $(seq 1 20); do
            curl -fsS "http://localhost:${REPLAY_PORT}/__health" > /dev/null 2>&1 && break
            sleep 0.5
        done
        curl -fsS "http://localhost:${REPLAY_PORT}/__health" > /dev/null || fail "metadata replay proxy did not start"
        PROXY="http://host.docker.internal:${REPLAY_PORT}"
        METADATA_ARGS=(--add-host=host.docker.internal:host-gateway
            -e "APP__METADATA__MUSICBRAINZ_BASE_URL=${PROXY}/mb/ws/2/"
            -e "APP__METADATA__COVER_ART_ARCHIVE_BASE_URL=${PROXY}/caa/"
            -e "APP__METADATA__DEEZER_BASE_URL=${PROXY}/deezer/"
            -e "APP__METADATA__ITUNES_BASE_URL=${PROXY}/itunes/")
        # A replay answers instantly and is not musicbrainz.org: no need to wait a second per request.
        # Recording talks to the real service through the proxy, so it keeps the 1 req/s spacing.
        if [ "$METADATA" = replay ]; then
            METADATA_ARGS+=(-e "APP__METADATA__MUSICBRAINZ_MIRROR_INTERVAL_MS=0")
        fi
        ;;
    live | off) ;;
    *) fail "SMOKE_METADATA must be replay, record, live or off (got '$METADATA')" ;;
esac

$DOCKER run -d --name "$NAME" \
    "${METADATA_ARGS[@]}" \
    -p "${PORT}:1077" \
    -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC \
    -e APP__SERVER__URL_BASE="$URL_BASE" \
    -v "$WORK/config:/config" -v "$WORK/data:/data" \
    "$IMAGE" > /dev/null
wait_healthy
pass "container healthy (HEALTHCHECK on /ping)"

curl -fsS "http://localhost:${PORT}/ping" | jq -e '.status == "OK"' > /dev/null || fail "/ping"
curl -fsS "${BASE}/ping" > /dev/null || fail "${URL_BASE}/ping"
pass "/ping at the root and under ${URL_BASE}"

KEY="$($DOCKER exec "$NAME" sh -c "sed -n 's/^ *api_key: *//p' /config/config.yml" | tr -d '\"'\''\r ')"
[ "${#KEY}" -eq 32 ] || fail "could not read the generated API key from config.yml"

code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}/api/v1/system/status")"
[ "$code" = 401 ] || fail "status without a key returned $code, expected 401"
STATUS="$(curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/system/status")"
echo "$STATUS" | jq -e --arg ub "$URL_BASE" '.appName == "Wondarr" and .urlBase == $ub and .isDocker == true' > /dev/null \
    || fail "system/status: $STATUS"
curl -fsS "${BASE}/api/v1/system/status?apikey=$KEY" > /dev/null || fail "?apikey= rejected"
curl -fsS -H "Authorization: Bearer $KEY" "${BASE}/api/v1/system/status" > /dev/null || fail "Bearer rejected"
pass "API key required and accepted (header, query, bearer); system/status reports Docker and the URL base"

# The host reaches the container through the Docker bridge, a private address, so the
# default "disabled for local addresses" lets the UI through without a login.
HTML="$(curl -fsS "${BASE}/")"
echo "$HTML" | grep -q "<base href=\"${URL_BASE}/\"" || fail "UI index.html lacks <base href=\"${URL_BASE}/\" />"
ASSET="$(echo "$HTML" | grep -oE '(src|href)="\./assets/[^"]+\.js"' | head -1 | sed -E 's/.*"\.\/(assets[^"]+)"/\1/')"
[ -n "$ASSET" ] || fail "no script asset referenced by index.html"
curl -fsS -o /dev/null "${BASE}/${ASSET}" || fail "asset ${ASSET} not served under the URL base"
curl -fsS "${BASE}/system/status" | grep -q "<base href=\"${URL_BASE}/\"" || fail "SPA deep link not served"
curl -fsS "${BASE}/initialize.json" | jq -e --arg ub "$URL_BASE" '.urlBase == $ub and (.apiKey | length) == 32' > /dev/null \
    || fail "initialize.json"
pass "UI, assets, deep links and initialize.json under ${URL_BASE}"

# slskd needs a few seconds after the app is up; health is 503 until every check is ok
for _ in $(seq 1 30); do
    HEALTH="$(curl -sS -H "X-Api-Key: $KEY" "${BASE}/api/v1/health?refresh=true")"
    echo "$HEALTH" | jq -e 'length >= 4 and all(.[]; .type == "ok")' > /dev/null 2>&1 && break
    sleep 2
done
echo "$HEALTH" | jq -e 'length >= 4 and all(.[]; .type == "ok")' > /dev/null || fail "health not all ok: $HEALTH"
echo "$HEALTH" | jq -e 'any(.[]; (.source | ascii_downcase | test("slskd")) and (.message | test("not configured")))' > /dev/null \
    || fail "health has no running, not-configured slskd entry: $HEALTH"
pass "health: $(echo "$HEALTH" | jq -r '[.[] | .source] | join(", ")') all ok; slskd running, Soulseek not configured"

ID="$(curl -fsS -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"name":"Heartbeat"}' "${BASE}/api/v1/command" | jq -r '.id')"
for _ in $(seq 1 30); do
    state="$(curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/command/$ID" | jq -r '.status')"
    [ "$state" = completed ] && break
    sleep 1
done
[ "$state" = completed ] || fail "Heartbeat command ended as '$state'"
BEFORE="$(curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/system/task" | jq -r '.[] | select(.taskName == "Heartbeat") | .lastExecution')"
[ -n "$BEFORE" ] && [ "$BEFORE" != null ] || fail "Heartbeat task has no last execution"
pass "POST /api/v1/command Heartbeat completed; task last run $BEFORE"

$DOCKER restart "$NAME" > /dev/null
wait_healthy
AFTER="$(curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/system/task" | jq -r '.[] | select(.taskName == "Heartbeat") | .lastExecution')"
[ -n "$AFTER" ] && [ "$AFTER" != null ] || fail "Heartbeat last execution lost across restart"
[[ "$AFTER" > "$BEFORE" || "$AFTER" == "$BEFORE" ]] || fail "last execution went backwards ($BEFORE -> $AFTER)"
pass "scheduled job state survives a restart ($AFTER)"

OWNER="$($DOCKER exec "$NAME" stat -c '%u:%g' /config/wondarr.db)"
[ "$OWNER" = "${PUID_WANT}:${PGID_WANT}" ] || fail "wondarr.db owned by $OWNER, expected ${PUID_WANT}:${PGID_WANT}"
PROC_USER="$($DOCKER exec "$NAME" sh -c 'ps -o user= -C dotnet 2>/dev/null | head -1 || true')"
pass "files carry PUID:PGID ($OWNER)${PROC_USER:+; app runs as $PROC_USER}"

$DOCKER exec -u "${PUID_WANT}:${PGID_WANT}" "$NAME" sh -c \
    'ffprobe -version >/dev/null && fpcalc -version >/dev/null && deno --version >/dev/null && /opt/slskd/slskd --version >/dev/null' \
    || fail "bundled tools do not all run"
pass "ffprobe, fpcalc, deno and slskd run inside the image"

echo "PHASE 0 GATE: PASS ($IMAGE)"

if [ "$METADATA" = off ]; then
    echo "Phase 1 gate skipped (SMOKE_METADATA=off)"
    exit 0
fi
python3 "$(dirname "$0")/phase1-gate.py" --url "$BASE" --api-key "$KEY" || fail "Phase 1 gate"
if [ "$METADATA" = replay ]; then
    MISSES="$(curl -fsS "http://localhost:${REPLAY_PORT}/__misses")"
    [ "$(echo "$MISSES" | jq 'length')" = 0 ] \
        || fail "the app asked for metadata tests/gate/replay has no recording of (re-record with SMOKE_METADATA=record): $MISSES"
    pass "every metadata request was answered from the recording"
fi
echo "PHASE 1 GATE: PASS ($IMAGE, metadata: $METADATA)"

if [ "${SMOKE_PHASE2:-fake}" = off ]; then
    echo "Phase 2 gate skipped (SMOKE_PHASE2=off)"
    exit 0
fi

# FakeSlskd is a test tool, never part of the image: publish it with the SDK image the Dockerfile uses.
REPO="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -m)" in aarch64 | arm64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
mkdir -p "$WORK/fake" && chmod 777 "$WORK/fake"
$DOCKER run --rm -v "$REPO:/src:ro" -v "$WORK/fake:/out" -e DOTNET_CLI_TELEMETRY_OPTOUT=1     mcr.microsoft.com/dotnet/sdk:10.0-noble sh -c     "mkdir -p /tmp/src/tools && cp /src/global.json /src/Directory.Build.props /src/Directory.Packages.props /src/.editorconfig /tmp/src/ && cp -r /src/tools/FakeSlskd /tmp/src/tools/ && rm -rf /tmp/src/tools/FakeSlskd/bin /tmp/src/tools/FakeSlskd/obj && cd /tmp/src && dotnet publish tools/FakeSlskd -c Release -r $RID --self-contained -p:PublishSingleFile=true -o /out -v q --nologo"     > "$WORK/fake-publish.log" 2>&1 || { tail -30 "$WORK/fake-publish.log" >&2; fail "could not publish FakeSlskd"; }
pass "FakeSlskd published ($RID)"

# The Phase 6 stage, a function so SMOKE_ONLY_PHASE6=on can run it straight after Phase 1 (to record its
# metadata without running Phases 2-5 first).
run_phase6() {

    # --- Phase 6: import lists, playlists, albums, libraries and conversion, on a fresh container -------
    # FakeSlskd stands in for slskd (files are offered as the gate needs them), answers AcoustID, and runs
    # a fake Plex Media Server (and plex.tv's server list) on 127.0.0.1:5033 with sections 1 = /data/music
    # and 2 = /data/music2. Metadata comes from the recordings like every other phase. Searching is kept
    # to the gate's own two cycles of ten songs: search on add is off.
    P6="${NAME}-p6"
    P6_PORT=$((PORT + 3))
    P6_BASE="http://localhost:${P6_PORT}${URL_BASE}"
    mkdir -p "$WORK/p6/config" "$WORK/p6/data/music" "$WORK/p6/data/music2" && chmod -R 777 "$WORK/p6"
    $DOCKER run -d --name "$P6"     "${METADATA_ARGS[@]}"     -p "${P6_PORT}:1077"     -e APP__LYRICS__ENABLED=false     -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC     -e APP__SERVER__URL_BASE="$URL_BASE"     -e APP__SOULSEEK__BINARY_PATH=/opt/fake/slskd -e APP__SOULSEEK__USERNAME=gate-user -e APP__SOULSEEK__PASSWORD=gate-password     -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/     -e APP__SEARCH__SEARCH_ON_ADD=false -e APP__SEARCH__MISSING_BATCH_SIZE=10     -e APP__PLEX__PLEX_TV_BASE_URL=http://127.0.0.1:5033/     -v "$WORK/p6/config:/config" -v "$WORK/p6/data:/data" -v "$WORK/fake:/opt/fake:ro"     "$IMAGE" > /dev/null
    for _ in $(seq 1 90); do
        [ "$($DOCKER inspect -f '{{.State.Health.Status}}' "$P6" 2> /dev/null)" = healthy ] && break
        sleep 2
    done
    P6_KEY="$($DOCKER exec "$P6" sh -c "sed -n 's/^ *api_key: *//p' /config/config.yml" | tr -d '\"'\''\r ')"
    [ "${#P6_KEY}" -eq 32 ] || fail "could not read the Phase 6 container's API key"
    P6_CURL="$DOCKER run --rm -i --network container:$P6 curlimages/curl:8.11.1 -fsS"
    P6_ARGS=(--url "$P6_BASE" --api-key "$P6_KEY" --state-in "$WORK/phase6-state.json" --state-out "$WORK/phase6-state.json")
    P6_FAKE_ADD="$P6_CURL -X POST -H 'Content-Type: application/json' --data-binary @- http://127.0.0.1:5030/fake/scenario/files"
    P6_PLEX_STATE="$P6_CURL http://127.0.0.1:5033/fake/plex"
    P6_EXEC="$DOCKER exec $P6"
    GATE6="$(dirname "$0")/phase6-gate.py"
    python3 "$GATE6" plex "${P6_ARGS[@]}" || { $DOCKER logs --tail 60 "$P6" >&2; fail "Phase 6 gate (Plex)"; }
    python3 "$GATE6" lists "${P6_ARGS[@]}" --csv "$REPO/tests/gate/phase6-exportify.csv"     --fake-add-cmd "$P6_FAKE_ADD" --plex-state-cmd "$P6_PLEX_STATE" --exec-cmd "$P6_EXEC"     || { $DOCKER logs --tail 80 "$P6" >&2; fail "Phase 6 gate (the 200-track list, downloads, the Plex playlist)"; }
    python3 "$GATE6" album "${P6_ARGS[@]}" || { $DOCKER logs --tail 60 "$P6" >&2; fail "Phase 6 gate (album add)"; }
    python3 "$GATE6" library "${P6_ARGS[@]}" --plex-state-cmd "$P6_PLEX_STATE" --exec-cmd "$P6_EXEC"     || { $DOCKER logs --tail 60 "$P6" >&2; fail "Phase 6 gate (a second library)"; }
    python3 "$GATE6" convert "${P6_ARGS[@]}" --fake-add-cmd "$P6_FAKE_ADD" --exec-cmd "$P6_EXEC"     || { $DOCKER logs --tail 60 "$P6" >&2; fail "Phase 6 gate (conversion)"; }
    if [ "$METADATA" = replay ]; then
        MISSES="$(curl -fsS "http://localhost:${REPLAY_PORT}/__misses")"
        [ "$(echo "$MISSES" | jq 'length')" = 0 ] || fail "the Phase 6 gate asked for metadata tests/gate/replay has no recording of (re-record with SMOKE_METADATA=record): $MISSES"
    fi
    $DOCKER rm -f "$P6" > /dev/null 2>&1 || true
    echo "PHASE 6 GATE: PASS ($IMAGE: a 200-track Exportify list, a Plex playlist kept in place, album add, a second library, conversion)"
}

if [ "${SMOKE_ONLY_PHASE6:-off}" = on ]; then
    run_phase6
    exit 0
fi

python3 "$(dirname "$0")/phase2-scenario.py" --url "$BASE" --api-key "$KEY" --out "$WORK/data/phase2-scenario.json" --keep 20     --dropped-out "$WORK/phase2-dropped.json" --reserve 5 --reserved-out "$WORK/phase2-reserved.json" || fail "could not build the Phase 2 scenario"
chmod 644 "$WORK/data/phase2-scenario.json"

# Same /config and /data (the songs stay); FakeSlskd stands in for slskd and answers AcoustID too.
# Lyrics come from LRCLIB, a live service: off unless SMOKE_LYRICS=live (an opt-in run, never CI).
LYRICS_ENABLED=false
[ "${SMOKE_LYRICS:-off}" = live ] && LYRICS_ENABLED=true
$DOCKER rm -f "$NAME" > /dev/null
$DOCKER run -d --name "$NAME"     "${METADATA_ARGS[@]}"     -p "${PORT}:1077"     -e APP__LYRICS__ENABLED="$LYRICS_ENABLED"     -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC     -e APP__SERVER__URL_BASE="$URL_BASE"     -e APP__SOULSEEK__BINARY_PATH=/opt/fake/slskd     -e APP__SOULSEEK__USERNAME=gate-user -e APP__SOULSEEK__PASSWORD=gate-password     -e FAKE_SLSKD_SCENARIO=/data/phase2-scenario.json     -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/     -v "$WORK/config:/config" -v "$WORK/data:/data" -v "$WORK/fake:/opt/fake:ro"     "$IMAGE" > /dev/null
wait_healthy
for _ in $(seq 1 30); do
    curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null 2>&1 && break
    sleep 2
done
curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null     || fail "FakeSlskd never reported a login: $(curl -sS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status")"
pass "FakeSlskd running in place of slskd, logged in"

python3 "$(dirname "$0")/phase2-gate.py" --url "$BASE" --api-key "$KEY" --rounds 2     --round-timeout-s 1200 --queue-timeout-s 1200 --min-ratio 0.80 --expect-caught 1 --share-toggle     --fake-log-cmd "$DOCKER run --rm --network container:$NAME curlimages/curl:8.11.1 -fsS http://127.0.0.1:5030/fake/log"     || fail "Phase 2 gate"
echo "PHASE 2 GATE: PASS ($IMAGE, FakeSlskd)"

if [ "${SMOKE_PHASE3:-on}" = off ]; then
    echo "Phase 3 gate skipped (SMOKE_PHASE3=off)"
    exit 0
fi

# The same container: the Phase 2 songs are in the library and FakeSlskd still answers AcoustID.
python3 "$(dirname "$0")/phase3-gate.py" --url "$BASE" --api-key "$KEY" --dropped "$WORK/phase2-dropped.json"     --exec "$DOCKER exec $NAME" --ref-root /data/reference --min-ratio 0.90 --timeout-s 1800     || fail "Phase 3 gate"
if [ "$METADATA" = replay ]; then
    MISSES="$(curl -fsS "http://localhost:${REPLAY_PORT}/__misses")"
    [ "$(echo "$MISSES" | jq 'length')" = 0 ]         || fail "the Phase 3 gate asked for metadata tests/gate/replay has no recording of (re-record with SMOKE_METADATA=record): $MISSES"
fi
echo "PHASE 3 GATE: PASS ($IMAGE, reference library + Match queue + adoption)"

if [ "${SMOKE_PHASE4:-on}" = off ]; then
    echo "Phase 4 gate skipped (SMOKE_PHASE4=off)"
    exit 0
fi

# Phase 4, on the same container: the YouTube source against the fake InnerTube and the fake yt-dlp.
# The app is restarted with the YouTube source enabled, its InnerTube client pointed at the fake's
# stub, yt-dlp pointed at the fake, and the AcoustID stub still answering. The image has no Python,
# so the fake yt-dlp is the FakeSlskd binary itself in its --fake-ytdlp mode, behind a sh wrapper.
mkdir -p "$WORK/fakeyt"
printf '#!/bin/sh\nexec /opt/fake/slskd --fake-ytdlp "$@"\n' > "$WORK/fakeyt/wrapper"
chmod 755 "$WORK/fakeyt/wrapper"

# The Phase 4 scenario: five songs the Phase 2 scenario reserved (kept unmonitored, nothing on the
# fake Soulseek) are monitored again — three Art-Track fills, one OMV case, one bot-check case. The
# fake InnerTube answers searches for them from the same scenario the fake yt-dlp reads.
python3 "$(dirname "$0")/phase4-scenario.py" --url "$BASE" --api-key "$KEY"     --reserved "$WORK/phase2-reserved.json" --out "$WORK/data/phase4-scenario.json"     || fail "could not build the Phase 4 scenario"
chmod 644 "$WORK/data/phase4-scenario.json"

$DOCKER rm -f "$NAME" > /dev/null
$DOCKER run -d --name "$NAME"     "${METADATA_ARGS[@]}"     -p "${PORT}:1077"     -e APP__LYRICS__ENABLED=false     -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC     -e APP__SERVER__URL_BASE="$URL_BASE"     -e APP__SOULSEEK__BINARY_PATH=/opt/fake/slskd -e APP__SOULSEEK__USERNAME=gate-user -e APP__SOULSEEK__PASSWORD=gate-password     -e FAKE_SLSKD_SCENARIO=/data/phase2-scenario.json     -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/     -e APP__YOUTUBE__ENABLED=true -e APP__YOUTUBE__ALLOW_VIDEOS=true     -e APP__YOUTUBE__BASE_URL=http://127.0.0.1:5032/     -e APP__YOUTUBE__YTDLP__BINARY_PATH=/opt/fakeyt/wrapper     -e FAKE_YT_SCENARIO=/data/phase4-scenario.json     -e FAKE_ACOUSTID_REGISTER=http://127.0.0.1:5031/v2/register     -e FAKE_INNERTUBE_FIXTURES=/fixtures/ytmusic     -v "$WORK/config:/config" -v "$WORK/data:/data" -v "$WORK/fake:/opt/fake:ro" -v "$WORK/fakeyt:/opt/fakeyt:ro"     -v "$REPO/tests/fixtures/ytmusic:/fixtures/ytmusic:ro"     "$IMAGE" > /dev/null
wait_healthy
for _ in $(seq 1 30); do
    curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null 2>&1 && break
    sleep 2
done
curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null     || fail "FakeSlskd never reported a login after the Phase 4 restart"
pass "the container is back, FakeSlskd in place, the YouTube source enabled"

python3 "$(dirname "$0")/phase4-gate.py" --url "$BASE" --api-key "$KEY" --scenario "$WORK/data/phase4-scenario.json"     --fake-log-cmd "$DOCKER run --rm --network container:$NAME curlimages/curl:8.11.1 -fsS http://127.0.0.1:5030/fake/log"     --rounds 2 --round-timeout-s 1200 --queue-timeout-s 1200     || fail "Phase 4 gate"
echo "PHASE 4 GATE: PASS ($IMAGE, the fake yt-dlp + the fake InnerTube)"

if [ "${SMOKE_PHASE5:-on}" = off ]; then
    echo "Phase 5 gate skipped (SMOKE_PHASE5=off)"
    exit 0
fi

# --- Phase 5a: the upgrade, on the same container -----------------------------------------------
# A song the Phase 2 gate imported at MP3-320 gets a FLAC-cutoff profile; a FLAC of it appears on
# FakeSlskd (POST /fake/scenario/files); UpgradeSearch replaces the file and recycles the old one.
python3 "$(dirname "$0")/phase5-gate.py" upgrade --url "$BASE" --api-key "$KEY" \
    --scenario "$WORK/data/phase2-scenario.json" \
    --fake-add-cmd "$DOCKER run --rm -i --network container:$NAME curlimages/curl:8.11.1 -fsS -X POST -H 'Content-Type: application/json' --data-binary @- http://127.0.0.1:5030/fake/scenario/files" \
    --exec-cmd "$DOCKER exec $NAME" --timeout-s 1200 \
    || fail "Phase 5 gate (upgrade)"

# --- Phase 5b: a backup restores into a fresh container ------------------------------------------
python3 "$(dirname "$0")/phase5-gate.py" backup-take --url "$BASE" --api-key "$KEY" \
    --zip "$WORK/phase5-backup.zip" --snapshot "$WORK/phase5-snapshot.json" \
    || fail "Phase 5 gate (backup)"

FRESH="${NAME}-fresh"
FRESH_PORT=$((PORT + 1))
FRESH_BASE="http://localhost:${FRESH_PORT}${URL_BASE}"
mkdir -p "$WORK/fresh/config" "$WORK/fresh/data" && chmod 777 "$WORK/fresh/config" "$WORK/fresh/data"
# The same environment as the gate container (the env-set Soulseek fields are part of the settings the
# snapshot compares); FakeSlskd stands in for slskd so the fresh container never reaches the network.
$DOCKER run -d --name "$FRESH" \
    "${METADATA_ARGS[@]}" \
    -p "${FRESH_PORT}:1077" \
    -e APP__LYRICS__ENABLED=false \
    -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC \
    -e APP__SERVER__URL_BASE="$URL_BASE" \
    -e APP__SOULSEEK__BINARY_PATH=/opt/fake/slskd -e APP__SOULSEEK__USERNAME=gate-user -e APP__SOULSEEK__PASSWORD=gate-password \
    -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/ \
    -v "$WORK/fresh/config:/config" -v "$WORK/fresh/data:/data" -v "$WORK/fake:/opt/fake:ro" \
    "$IMAGE" > /dev/null
for _ in $(seq 1 90); do
    [ "$($DOCKER inspect -f '{{.State.Health.Status}}' "$FRESH" 2> /dev/null)" = healthy ] && break
    sleep 2
done
FRESH_KEY="$($DOCKER exec "$FRESH" sh -c "sed -n 's/^ *api_key: *//p' /config/config.yml" | tr -d '\"'\''\r ')"
[ "${#FRESH_KEY}" -eq 32 ] || fail "could not read the fresh container's API key"
[ "$FRESH_KEY" != "$KEY" ] || fail "the fresh container has the gate container's API key before the restore"
python3 "$(dirname "$0")/phase5-gate.py" backup-verify --url "$FRESH_BASE" --api-key "$FRESH_KEY" \
    --zip "$WORK/phase5-backup.zip" --snapshot "$WORK/phase5-snapshot.json" --timeout-s 300 \
    || { $DOCKER logs --tail 60 "$FRESH" >&2; fail "Phase 5 gate (backup → fresh container)"; }
$DOCKER rm -f "$FRESH" > /dev/null 2>&1 || true

# --- Phase 5c: the Phase 2 gate, unchanged, against an external slskd -----------------------------
# A fresh container in external mode, pointed at FakeSlskd running as a separate container that shares
# its network namespace (so the loopback-only AcoustID stub stays reachable) and its /data (so the
# downloads are where soulseek.downloads_dir says). The library is built the way Phases 1 and 2 build
# it: the 50-song paste, then the Phase 2 scenario.
EXT="${NAME}-ext"
EXT_FAKE="${NAME}-extfake"
EXT_PORT=$((PORT + 2))
EXT_BASE="http://localhost:${EXT_PORT}${URL_BASE}"
EXT_SLSKD_KEY="phase5-external-gate-key-0123456789abcdef"
mkdir -p "$WORK/ext/config" "$WORK/ext/data/downloads/slskd/incomplete" "$WORK/ext/data/fake" \
    && chmod -R 777 "$WORK/ext"
cat > "$WORK/ext/data/fake/slskd.yml" <<EOF
web:
  port: 5030
  ip_address: 127.0.0.1
  authentication:
    api_keys:
      wondarr:
        key: ${EXT_SLSKD_KEY}
soulseek:
  username: gate-external-user
directories:
  downloads: /data/downloads/slskd
  incomplete: /data/downloads/slskd/incomplete
EOF
$DOCKER run -d --name "$EXT" \
    "${METADATA_ARGS[@]}" \
    -p "${EXT_PORT}:1077" \
    -e APP__LYRICS__ENABLED=false \
    -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC \
    -e APP__SERVER__URL_BASE="$URL_BASE" \
    -e APP__SOULSEEK__MODE=external \
    -e APP__SOULSEEK__EXTERNAL__URL=http://127.0.0.1:5030 \
    -e APP__SOULSEEK__EXTERNAL__API_KEY="$EXT_SLSKD_KEY" \
    -e APP__SOULSEEK__DOWNLOADS_DIR=/data/downloads/slskd \
    -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/ \
    -v "$WORK/ext/config:/config" -v "$WORK/ext/data:/data" \
    "$IMAGE" > /dev/null
for _ in $(seq 1 90); do
    [ "$($DOCKER inspect -f '{{.State.Health.Status}}' "$EXT" 2> /dev/null)" = healthy ] && break
    sleep 2
done
EXT_KEY="$($DOCKER exec "$EXT" sh -c "sed -n 's/^ *api_key: *//p' /config/config.yml" | tr -d '\"'\''\r ')"
[ "${#EXT_KEY}" -eq 32 ] || fail "could not read the external-mode container's API key"
python3 "$(dirname "$0")/phase1-gate.py" --url "$EXT_BASE" --api-key "$EXT_KEY" > "$WORK/ext-phase1.log" 2>&1 \
    || { tail -20 "$WORK/ext-phase1.log" >&2; fail "the external-mode library could not be built (Phase 1 paste)"; }
python3 "$(dirname "$0")/phase2-scenario.py" --url "$EXT_BASE" --api-key "$EXT_KEY" \
    --out "$WORK/ext/data/phase2-scenario.json" --keep 20 \
    || fail "could not build the external-mode Phase 2 scenario"
chmod 644 "$WORK/ext/data/phase2-scenario.json"
$DOCKER run -d --name "$EXT_FAKE" --network "container:$EXT" --user "${PUID_WANT}:${PGID_WANT}" \
    --entrypoint /opt/fake/slskd \
    -e SLSKD_CONFIG=/data/fake/slskd.yml -e SLSKD_APP_DIR=/data/fake \
    -e FAKE_SLSKD_SCENARIO=/data/phase2-scenario.json \
    -v "$WORK/ext/data:/data" -v "$WORK/fake:/opt/fake:ro" \
    "$IMAGE" > /dev/null
for _ in $(seq 1 60); do
    curl -fsS -H "X-Api-Key: $EXT_KEY" "${EXT_BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null 2>&1 && break
    sleep 2
done
curl -fsS -H "X-Api-Key: $EXT_KEY" "${EXT_BASE}/api/v1/soulseek/status" | jq -e '.mode == "external" and .loggedIn == true' > /dev/null \
    || { $DOCKER logs --tail 40 "$EXT_FAKE" >&2; fail "the external FakeSlskd never reported a login: $(curl -sS -H "X-Api-Key: $EXT_KEY" "${EXT_BASE}/api/v1/soulseek/status")"; }
pass "external-slskd mode: Wondarr polls FakeSlskd as the user's own slskd, logged in"
# The Phase 2 gate exactly as above, minus the share toggle: in external mode the shares belong to the
# user's slskd, not to Wondarr's settings.
python3 "$(dirname "$0")/phase2-gate.py" --url "$EXT_BASE" --api-key "$EXT_KEY" --rounds 2 \
    --round-timeout-s 1200 --queue-timeout-s 1200 --min-ratio 0.80 --expect-caught 1 \
    --fake-log-cmd "$DOCKER run --rm --network container:$EXT curlimages/curl:8.11.1 -fsS http://127.0.0.1:5030/fake/log" \
    || { $DOCKER logs --tail 60 "$EXT" >&2; fail "Phase 2 gate in external-slskd mode"; }
$DOCKER rm -f "$EXT_FAKE" "$EXT" > /dev/null 2>&1 || true
echo "PHASE 5 GATE: PASS ($IMAGE: upgrade, backup → fresh container, external slskd)"

if [ "${SMOKE_PHASE6:-off}" = off ]; then
    echo "Phase 6 gate skipped (SMOKE_PHASE6=off)"
    exit 0
fi
run_phase6
