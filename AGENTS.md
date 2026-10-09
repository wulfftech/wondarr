# AGENTS.md — Wondarr

Wondarr is a self-hosted *arr for **single songs**: Soulseek (bundled slskd) → YouTube Music (yt-dlp) → torrents/usenet (qBittorrent, SABnzbd), verified imports (duration + AcoustID), Lidarr-style profiles/upgrades, and library layouts including a Plexamp preset. C#/.NET 10 + React 19. GPL-3.0. HTTP port 1077.

**Start every session by reading `docs/HANDOVER.md` and `docs/build/PROGRESS.md`, then pick the next task from `docs/build/PHASE_<n>_TASKS.md`.**

## Document precedence

1. `docs/DECISIONS.md` and `docs/adr/` — decisions are binding; change them only with a new dated entry/ADR.
2. `docs/architecture/*.md`, `docs/product/*.md`, `docs/build/*.md` — living specs; keep them in sync with code in the same change.
3. `docs/PLAN.md` — the frozen planning record; historical when it disagrees with the above.
4. `docs/research/` — evidence with URLs; re-verify claims tagged as search-extract before quoting.

## How we build (summary of `docs/build/AGENT_WORKFLOW.md`)

- **This session is the orchestrator** (Claude Code on Claude Opus 5.5, `/model claude-opus-5-5`). It owns design, task specs, code review, verification and commits. It does *not* hand-type large volumes of routine code.
- **Implementation is delegated to Claude Code subagents** (Agent tool; DECISIONS 2026-10-09 build session 10 #1 — OpenRouter and `scripts/worker.py` are paused): an ordinary feature, test or frontend task goes to a `general-purpose` subagent on `sonnet` in its own worktree (`isolation: worktree`, in the background); mechanical work (porting one file, boilerplate tests, doc drafts, fixtures, searches) to a `haiku` subagent (`Explore` for read-only searches). Hard backend work (concurrency, the import pipeline, migrations, release engineering) the orchestrator may write itself. Subagents get a self-contained task file (`docs/build/WORKER_TASK_TEMPLATE.md`) plus the branch, allowed paths and commands, never design authority. At most three at once.
- **Loop**: spec (committed) → subagent → the orchestrator runs build/tests in its worktree → review → fix or merge through `scripts/safe-merge.sh` → update `PROGRESS.md` → commit. Anything touching processes, files, time, the database, auth or external limits also gets a review subagent (`sonnet`, no worktree, `git diff main...<branch>` against `CODING_STANDARDS.md` and the spec); the orchestrator verifies every finding before acting on it.
- Never merge red. Never let a subagent touch `docs/DECISIONS.md`, `docs/adr/`, `AGENTS.md`, `CLAUDE.md`, `.claude/`, `.github/workflows/` or `.env*`.

## Non-negotiables

- Do not embed Soulseek.NET or any Soulseek client library; Soulseek goes through the bundled slskd process over HTTP (ADR-0004).
- Do not copy AGPL code (Sockseek, slskd). Port GPL-3.0 code from Lidarr/Prowlarr/Sonarr and MIT code from SoulSync/spotDL **with attribution headers and a `NOTICE.md` entry** (ADR-0002, ADR-0003).
- Never write `.webm`; never produce a lossless container from a lossy source; a transcoded YouTube file keeps quality `OPUS-160` (ADR-0006, ADR-0008).
- Respect external limits: Soulseek ≤ 30 searches / 4 min and ≤ 2 outstanding; MusicBrainz 1 req/s with a descriptive User-Agent; AcoustID 3 req/s; honour `Retry-After` everywhere.
- Keep the *arr API conventions (`X-Api-Key`, `/api/v1`, `/ping`, `/api/v1/system/status`, queue/wanted/history/blocklist shapes) and Docker conventions (`/config`, `/data`, `PUID/PGID/UMASK/TZ`) (ADR-0010).
- Album folders are decided by the album policy and are **sticky**; re-assignment only via the explicit Compact task (ADR-0007).
- No secrets in the repo or in commit messages; no model names or AI-session identifiers in commit messages.
- Docs move with code; new decisions get a dated entry in `docs/DECISIONS.md`.

## Commands (keep this list current)

```
dotnet build -warnaserror && dotnet test          # backend
cd frontend && npm ci && npm run lint && npm run typecheck && npm test && npm run build
docker compose -f docker/docker-compose.yml up --build
scripts/safe-merge.sh <branch>                               # merge a task branch only if the tree is green before and after
scripts/smoke-test.sh <image> ["sudo docker"]                # Phase 0 + Phase 1 gates against a built image (SMOKE_METADATA=replay|record|live|off)
python scripts/phase1-gate.py --url <base> --api-key <key>   # the Phase 1 gate against any running instance
python scripts/phase5-gate.py upgrade|backup-take|backup-verify --url <base> --api-key <key> …   # Phase 5 gate pieces (SMOKE_PHASE5 runs them in the smoke test)
python scripts/phase6-gate.py plex|lists|album|library|convert --url <base> --api-key <key> …   # Phase 6 gate pieces (SMOKE_PHASE6 runs them; SMOKE_ONLY_PHASE6=on skips Phases 2-5; record new metadata with the "Record gate metadata" workflow)
python scripts/phase7-gate.py setup|torrent|usenet --url <base> --api-key <key> [--fake-cmd "…curl…"]   # Phase 7 gate pieces (SMOKE_PHASE7 runs them; SMOKE_ONLY_PHASE7=on skips Phases 2-6)
python scripts/metadata-replay.py --mode record|replay --dir tests/gate/replay   # record/replay MusicBrainz, CAA, Deezer, iTunes
python scripts/check-notice.py [--fix]                       # ported-code attribution check (CI)
```

## Conventions

Conventional Commits; branches `phase<n>/<task-id>-<slug>`; subagent worktrees under `.claude/worktrees/` (the Agent tool's) or `.worktrees/`; task files under `docs/build/tasks/`; progress in `docs/build/PROGRESS.md`; coding rules in `docs/build/CODING_STANDARDS.md`; layout in `docs/build/REPO_LAYOUT.md`.
