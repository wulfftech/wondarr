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
- **Implementation is delegated to cheap workers**: `python scripts/worker.py run docs/build/tasks/<id>.md` runs a sandboxed tool-calling agent loop (`scripts/worker_agent.py`) directly against the **OpenRouter** API (`OPENROUTER_API_KEY` in `.env`; model per task from `docs/build/MODEL_VALUE_MATRIX.md`) inside a git worktree. No CLI or Anthropic account is involved. Workers get a self-contained task file (`docs/build/WORKER_TASK_TEMPLATE.md`), never design authority.
- **Loop**: spec → worker → build/tests → orchestrator reviews the diff → fix or merge → update `PROGRESS.md` → commit. Risky areas also get `python scripts/worker.py review <branch>`.
- Never merge red. Never let a worker touch `docs/DECISIONS.md`, `docs/adr/`, `AGENTS.md`, or `CLAUDE.md`.

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
python scripts/worker.py run docs/build/tasks/P0-01.md      # delegate a task to a cheap worker
python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md
python scripts/worker.py watch                               # follow running workers' live logs
scripts/smoke-test.sh <image> ["sudo docker"]                # Phase 0 + Phase 1 gates against a built image (SMOKE_METADATA=replay|record|live|off)
python scripts/phase1-gate.py --url <base> --api-key <key>   # the Phase 1 gate against any running instance
python scripts/metadata-replay.py --mode record|replay --dir tests/gate/replay   # record/replay MusicBrainz, CAA, Deezer, iTunes
python scripts/check-notice.py [--fix]                       # ported-code attribution check (CI)
```

## Conventions

Conventional Commits; branches `phase<n>/<task-id>-<slug>`; worker worktrees under `.worktrees/`; task files under `docs/build/tasks/`; progress in `docs/build/PROGRESS.md`; coding rules in `docs/build/CODING_STANDARDS.md`; layout in `docs/build/REPO_LAYOUT.md`.
