# ADR-0010: *arr-compatible API, Docker conventions, port 1077

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Dashboards (Homepage/Homarr), Unpackerr and autobrr read Lidarr-shaped endpoints; users know the *arr Docker layout (`docs/research/research_arr.md` §4). The owner asked for alignment with the other *arrs and chose port 1077, which no *arr-family app uses.

## Decision
API under `/api/v1` with `X-Api-Key` (also `apikey` query and Bearer), Forms auth with "disabled for local addresses", URL base, `/ping`, `/api/v1/system/status`, `/api/v1/health`, Lidarr-shaped `queue`, `queue/status`, `wanted/missing|cutoff`, `history`, `blocklist`, `command`, `release`, `release/push`, `calendar`. Docker: hotio/LinuxServer-style image with `/config` and a single `/data` mount, `PUID/PGID/UMASK/TZ`, s6-overlay init, `HEALTHCHECK` on `/ping`, multi-arch images on GHCR, Unraid template. HTTP port **1077**; Soulseek listen port 50300 exposed for the bundled slskd; slskd's own 5030 stays internal.

## Consequences
- Existing tooling works with a "Lidarr-compatible" toggle; contract tests guard the shapes.
- Prowlarr cannot register the app (hard-coded app list); indexers are added by URL + key.

## References
`docs/architecture/ARCHITECTURE.md` §5.6 · `docs/architecture/DEPLOYMENT.md` §9.2 · `docs/DECISIONS.md` round 2 Q8
