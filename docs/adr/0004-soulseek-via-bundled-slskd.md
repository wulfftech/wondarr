# ADR-0004: Soulseek via a bundled, app-supervised slskd process; never an embedded client library

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Soulseek.NET, the only production-grade embeddable client, is GPL-3.0-only with policed "additional terms" (unique client version registration, revocation) and its NuGet packages are unlisted/deprecated (`docs/research/research_stack.md` §5.1). The network expects full clients that share, and bans clients that search too fast (`docs/research/research_soulseek.md` §3). slskd is a complete client with an HTTP API, headless mode, YAML hot-reload and webhooks (§1). The owner wants slskd deployed/packaged with the app and sharing configurable in-app.

## Decision
The app image bundles a pinned slskd release under `/opt/slskd`. A `SlskdHost` hosted service renders `/config/slskd/slskd.yml` from the app's Soulseek settings (credentials, listen port, shared folders, "Share my library" toggle, slots, limits, directories, distributed network), launches slskd headless with a generated API key bound to localhost, captures logs, monitors health/login state, and restarts it when a change requires it. All Soulseek search/download traffic goes through slskd's `/api/v0` HTTP API. An *external slskd* mode (URL + API key + options API via JWT) exists for users who already run one. The app never links a Soulseek client library.

## Consequences
- slskd's API is v0/unstable: isolate it behind an interface, version-check at startup, contract-test against Swagger, pin the version per release.
- Sharing is on by default; turning it off shows a persistent warning (network etiquette).
- One login per username: the docs require a dedicated Soulseek account; a duplicate-login kick is surfaced by health checks (slskd does not auto-reconnect).
- Search budget: ≤ 30 searches / 4 min, ≤ 2 outstanding, ≥ 5 s spacing; searches deleted after use; wall-clock cancel for stuck searches.

## References
`docs/architecture/DEPLOYMENT.md` §9.1, §9.5 · `docs/architecture/MATCHING_ENGINE.md` §6.4 · `docs/research/research_soulseek.md`
