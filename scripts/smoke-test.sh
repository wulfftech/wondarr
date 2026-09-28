#!/usr/bin/env bash
# Phase 0 gate smoke test (docs/build/PHASES.md): runs an image and checks that
#   - the container starts on 1077 and turns healthy
#   - the UI loads behind a URL base
#   - the API key works (and is required)
#   - health shows DB, folders and the bundled slskd process OK (logged out, no credentials)
#   - a scheduled no-op job's last run survives a restart
#   - files the app creates carry PUID/PGID, and the bundled tools run
#
# usage: scripts/smoke-test.sh <image> [docker command, e.g. "sudo docker"]
# needs: bash, curl, jq
set -euo pipefail

IMAGE="${1:?image}"
DOCKER="${2:-docker}"
NAME="compilarr-smoke-$$"
PORT="${SMOKE_PORT:-1077}"
URL_BASE="/compilarr"
PUID_WANT="${SMOKE_PUID:-1234}"
PGID_WANT="${SMOKE_PGID:-2345}"
WORK="$(mktemp -d)"
BASE="http://localhost:${PORT}${URL_BASE}"

cleanup() {
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

$DOCKER run -d --name "$NAME" \
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
echo "$STATUS" | jq -e --arg ub "$URL_BASE" '.appName == "Compilarr" and .urlBase == $ub and .isDocker == true' > /dev/null \
    || fail "system/status: $STATUS"
curl -fsS "${BASE}/api/v1/system/status?apikey=$KEY" > /dev/null || fail "?apikey= rejected"
curl -fsS -H "Authorization: Bearer $KEY" "${BASE}/api/v1/system/status" > /dev/null || fail "Bearer rejected"
pass "API key required and accepted (header, query, bearer); system/status reports Docker and the URL base"

# The host reaches the container through the Docker bridge, a private address, so the
# default "disabled for local addresses" lets the UI through without a login.
HTML="$(curl -fsS "${BASE}/")"
echo "$HTML" | grep -q "<base href=\"${URL_BASE}/\">" || fail "UI index.html lacks <base href=\"${URL_BASE}/\">"
ASSET="$(echo "$HTML" | grep -oE '(src|href)="\./assets/[^"]+\.js"' | head -1 | sed -E 's/.*"\.\/(assets[^"]+)"/\1/')"
[ -n "$ASSET" ] || fail "no script asset referenced by index.html"
curl -fsS -o /dev/null "${BASE}/${ASSET}" || fail "asset ${ASSET} not served under the URL base"
curl -fsS "${BASE}/system/status" | grep -q "<base href=\"${URL_BASE}/\">" || fail "SPA deep link not served"
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

OWNER="$($DOCKER exec "$NAME" stat -c '%u:%g' /config/compilarr.db)"
[ "$OWNER" = "${PUID_WANT}:${PGID_WANT}" ] || fail "compilarr.db owned by $OWNER, expected ${PUID_WANT}:${PGID_WANT}"
PROC_USER="$($DOCKER exec "$NAME" sh -c 'ps -o user= -C dotnet 2>/dev/null | head -1 || true')"
pass "files carry PUID:PGID ($OWNER)${PROC_USER:+; app runs as $PROC_USER}"

$DOCKER exec -u "${PUID_WANT}:${PGID_WANT}" "$NAME" sh -c \
    'ffprobe -version >/dev/null && fpcalc -version >/dev/null && deno --version >/dev/null && /opt/slskd/slskd --version >/dev/null' \
    || fail "bundled tools do not all run"
pass "ffprobe, fpcalc, deno and slskd run inside the image"

echo "PHASE 0 GATE: PASS ($IMAGE)"
