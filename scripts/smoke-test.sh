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
# Phase 2 (scripts/phase2-gate.py with tools/FakeSlskd; SMOKE_PHASE2=fake | off (default until the pipeline lands)):
#   - the bundled slskd is replaced by FakeSlskd (a scenario built from the Phase 1 songs, real
#     encoded audio, an AcoustID stand-in); MissingSearch imports >= 80 % of the wanted songs at or
#     above cutoff, a live take disguised as the best file is caught after download and the next
#     candidate imported, a rejected transfer falls through to the next candidate, the Soulseek
#     search budget holds, and toggling "Share my library" changes what slskd shares
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
    $DOCKER rm -f "$NAME" > /dev/null 2>&1 || true
    if [ "${KEEP_WORK:-0}" != 1 ]; then
        # files belong to PUID/PGID; remove them from inside a container to avoid needing root here
        $DOCKER run --rm -v "$WORK:/w" --entrypoint /bin/sh "$IMAGE" -c 'rm -rf /w/config /w/data' > /dev/null 2>&1 || true
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

if [ "${SMOKE_PHASE2:-off}" = off ]; then  # TODO(phase2): default to fake once the import pipeline is merged
    echo "Phase 2 gate skipped (SMOKE_PHASE2=off)"
    exit 0
fi

# FakeSlskd is a test tool, never part of the image: publish it with the SDK image the Dockerfile uses.
REPO="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -m)" in aarch64 | arm64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
mkdir -p "$WORK/fake" && chmod 777 "$WORK/fake"
$DOCKER run --rm -v "$REPO:/src:ro" -v "$WORK/fake:/out" -e DOTNET_CLI_TELEMETRY_OPTOUT=1     mcr.microsoft.com/dotnet/sdk:10.0-noble sh -c     "mkdir -p /tmp/src/tools && cp /src/global.json /src/Directory.Build.props /src/Directory.Packages.props /src/.editorconfig /tmp/src/ && cp -r /src/tools/FakeSlskd /tmp/src/tools/ && rm -rf /tmp/src/tools/FakeSlskd/bin /tmp/src/tools/FakeSlskd/obj && cd /tmp/src && dotnet publish tools/FakeSlskd -c Release -r $RID --self-contained -p:PublishSingleFile=true -o /out -v q --nologo"     > "$WORK/fake-publish.log" 2>&1 || { tail -30 "$WORK/fake-publish.log" >&2; fail "could not publish FakeSlskd"; }
pass "FakeSlskd published ($RID)"

python3 "$(dirname "$0")/phase2-scenario.py" --url "$BASE" --api-key "$KEY" --out "$WORK/data/phase2-scenario.json" --keep 20     || fail "could not build the Phase 2 scenario"
chmod 644 "$WORK/data/phase2-scenario.json"

# Same /config and /data (the songs stay); FakeSlskd stands in for slskd and answers AcoustID too.
$DOCKER rm -f "$NAME" > /dev/null
$DOCKER run -d --name "$NAME"     "${METADATA_ARGS[@]}"     -p "${PORT}:1077"     -e PUID="$PUID_WANT" -e PGID="$PGID_WANT" -e UMASK=002 -e TZ=Etc/UTC     -e APP__SERVER__URL_BASE="$URL_BASE"     -e APP__SOULSEEK__BINARY_PATH=/opt/fake/slskd     -e APP__SOULSEEK__USERNAME=gate-user -e APP__SOULSEEK__PASSWORD=gate-password     -e FAKE_SLSKD_SCENARIO=/data/phase2-scenario.json     -e APP__ACOUSTID__CLIENT_KEY=gate -e APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/     -v "$WORK/config:/config" -v "$WORK/data:/data" -v "$WORK/fake:/opt/fake:ro"     "$IMAGE" > /dev/null
wait_healthy
for _ in $(seq 1 30); do
    curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null 2>&1 && break
    sleep 2
done
curl -fsS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status" | jq -e '.loggedIn == true' > /dev/null     || fail "FakeSlskd never reported a login: $(curl -sS -H "X-Api-Key: $KEY" "${BASE}/api/v1/soulseek/status")"
pass "FakeSlskd running in place of slskd, logged in"

python3 "$(dirname "$0")/phase2-gate.py" --url "$BASE" --api-key "$KEY" --rounds 2     --round-timeout-s 1200 --queue-timeout-s 1200 --min-ratio 0.80 --expect-caught 1 --share-toggle     --fake-log-cmd "$DOCKER run --rm --network container:$NAME curlimages/curl:8.11.1 -fsS http://127.0.0.1:5030/fake/log"     || fail "Phase 2 gate"
echo "PHASE 2 GATE: PASS ($IMAGE, FakeSlskd)"
