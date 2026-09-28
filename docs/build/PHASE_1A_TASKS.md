# Phase 1a tasks — rename Wondarr to Wondarr

**Why (owner, 2026-09-28):** the name should say what the product is for: hunting down the one song an artist is known for (the stand-alone hit), **without** pulling in the artist's obscure 12-track album of B-sides. "Wondarr" nods to one-hit *wonders* and keeps the *arr suffix. Wondarr read as "compilations", which is at most a side effect of the album policy.

**When:** after the Phase 1 gate passes and before Phase 2. Doing it mid-Phase 1 would conflict with every open worker branch, since the rename touches ~350 files and every namespace.

**How:** the code rename is mechanical, so it is **done by the orchestrator with a script** (`git mv` + an exact, case-preserving replace: `Wondarr` → `Wondarr`, `wondarr` → `wondarr`, `WONDARR` → `WONDARR`), reviewed as one commit and verified by the full gate. Handing a cheap worker a 350-file diff would cost more and be harder to review. Workers only get the prose (P1a-02).

| ID | Title | Depends on | Tier |
|---|---|---|---|
| P1a-01 | Decision record: dated entry in `DECISIONS.md` superseding round 2 #1; name-collision check recorded; `CLAUDE.md` and `AGENTS.md`-style pointers updated | Phase 1 gate | orchestrator |
| P1a-02 | Product positioning: README tagline and intro, `docs/product/PRODUCT.md` / `GOALS.md` wording ("the *arr for one-hit wonders: the song you want, not the album of B-sides"), UI title and About text | P1a-01 | worker (single-shot, prose) |
| P1a-03 | Mechanical rename: solution, projects, assemblies, namespaces, `Wondarr.*` test projects, frontend package name and UI strings, OpenAPI title + snapshot, User-Agent (`Wondarr/<version>`), auth cookie `WondarrAuth`, env names in scripts (`WONDARR_LIVE_TESTS`, `WONDARR_UPDATE_OPENAPI`, `WONDARR_WORKER_MODEL` …), Docker (account `wondarr`, s6 services `init-wondarr-user`/`svc-wondarr`, labels, compose service/container), CI and release workflows (`ghcr.io/wulfftech/wondarr`), smoke test, docs outside the frozen/historical set | P1a-01 | orchestrator |
| P1a-04 | Upgrade path for the alpha installs: at startup, if `/config/wondarr.db` (+ `-wal`/`-shm`) exists and `wondarr.db` does not, rename it (logged once); the old `WondarrAuth` cookie simply expires (users sign in again); `.env` keys read under both names for one phase, with a warning | P1a-03 | worker |
| P1a-05 | Outside the repo (owner agreed the orchestrator does it): GitHub repo `wulfftech/wondarr` → `wulfftech/wondarr` via `gh repo rename` (GitHub redirects old URLs and git remotes), first `ghcr.io/wulfftech/wondarr` images from the release workflow (the owner sets the new package public: it needs a scope the session's token lacks), a final `ghcr.io/wulfftech/wondarr` note pointing at the new name; local folder `D:\Code\wondarr` → `D:\Code\wondarr`, the `claude-terminals` MCP registration and the session memory move with it | P1a-03 | owner + orchestrator |
| P1a-06 | Verification: build/tests/frontend checks, the full Phase 0 + Phase 1 gate on the new image in CI and on `ch01`, and `git grep -i wondarr` empty except the historical documents and the upgrade shim | all | orchestrator |

**Left as history (not renamed):** `docs/PLAN.md` (frozen planning record), `docs/research/**` (evidence), past entries in `docs/DECISIONS.md`, past Session log entries in `docs/HANDOVER.md`, and the `v0.0.1-alpha.1` tag. Each gets a one-line note at the top: "Wondarr was renamed Wondarr on 2026-09-28."

**Done when:** the Phase 0 and Phase 1 gates pass on `ghcr.io/wulfftech/wondarr:develop`; an existing `wondarr.db` from `0.0.1-alpha.1` is picked up after upgrading; no `wondarr` remains outside the historical set and the upgrade shim.

**Status (2026-09-29): done** — see `docs/build/PROGRESS.md` "Phase 1a". Left for the owner: renaming the local folder and re-adding the `claude-terminals` MCP for it.
