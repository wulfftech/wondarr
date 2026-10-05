# Contributing

Wondarr is a self-hosted \*arr for **single songs**, built by an AI-orchestrated workflow (see [`docs/build/AGENT_WORKFLOW.md`](docs/build/AGENT_WORKFLOW.md)) under the owner's direction. Human contributions are welcome once the first release is cut; until then, open an issue before starting work so it can be folded into the phase plan.

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 10.0.4xx — pinned by [`global.json`](global.json) to `10.0.401` with `latestPatch` | backend and tests |
| Node.js | ≥ 22.12, with npm | frontend lint, typecheck, tests, build |
| Docker | current, with the Compose plugin | building and running the image |
| Python | 3.x | only for `scripts/worker.py` |

A shell (bash, zsh or PowerShell) and `git` are assumed.

## Build and test

```bash
# backend
dotnet build -warnaserror && dotnet test

# frontend
cd frontend && npm ci && npm run lint && npm run typecheck && npm test && npm run build

# Docker image (web UI on 1077, Soulseek on 50300)
docker compose -f docker/docker-compose.yml up --build

# Phase 0 gate smoke test against a built image (needs Docker, curl, jq)
scripts/smoke-test.sh <image> [docker command]

# delegate a task to a cheap worker (see docs/build/AGENT_WORKFLOW.md)
python scripts/worker.py run docs/build/tasks/P0-01.md
python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md
```

These are exactly the commands listed under "Commands" in [`AGENTS.md`](AGENTS.md), plus `scripts/smoke-test.sh`. If you add a script, add it to that list instead of inventing a parallel one.

## Repository map

```
src/Wondarr.Core/          # domain, matching engine, import pipeline, tagging, persistence (no ASP.NET)
src/Wondarr.Api/           # ASP.NET Core host: /api/v1 controllers, auth, SignalR, SPA hosting
src/Wondarr.Sources.*/     # slskd, YouTube Music (yt-dlp), Torznab/torrent/usenet providers
frontend/                    # React 19 + TypeScript + Vite + Mantine + TanStack Query
tests/                       # xUnit unit/integration tests and recorded fixtures
docker/                      # Dockerfile, s6-overlay root files, docker-compose.yml
scripts/                     # worker.py, fetch-slskd.sh, smoke-test.sh, check-notice.py
docs/                        # product, architecture, build and research documentation
```

The full tree, including intended project and test names, is in [`docs/build/REPO_LAYOUT.md`](docs/build/REPO_LAYOUT.md).

## Coding rules

The binding rules are in [`docs/build/CODING_STANDARDS.md`](docs/build/CODING_STANDARDS.md) — read them before your first change. In brief:

- One task → one branch → one reviewable diff; never mix a refactor with a feature.
- Green before done: `dotnet build -warnaserror` and `dotnet test`; `npm run lint && npm run typecheck && npm test` for the frontend.
- C#: nullable enabled, warnings as errors, file-scoped namespaces, `record` DTOs, `async` with `CancellationToken` on every I/O call, DI through constructors, typed `HttpClient`s with per-host rate limits, EF Core migrations checked in, `TimeProvider` and a file-system abstraction instead of direct statics.
- Never log or commit secrets (API keys, Soulseek password, Plex token); `.env` is git-ignored.
- Tests: golden tests for parsing/scoring/naming, contract tests against recorded fixtures for every external API, integration tests via `WebApplicationFactory`.
- Docs move with code: a behavioural change updates the matching `docs/architecture/*.md` file in the same change.

## Commits and branches

[Conventional Commits](https://www.conventionalcommits.org/), imperative mood, scope in parentheses where useful (`feat(slskd): …`). Branch names are `phase<n>/<task-id>-<slug>`, e.g. `phase0/P0-12-contributor-docs`. Outside contributors open pull requests rather than pushing to `main`; never put model names or AI-session identifiers in commit messages.

## Ported code

Any file adapted from another project needs a header comment naming the source repository, the upstream path and the licence, plus a row in [`NOTICE.md`](NOTICE.md). `scripts/check-notice.py` runs in CI and fails when a `Ported from` header has no entry.

- Port from Lidarr, Prowlarr and Sonarr (GPL-3.0) and from SoulSync and spotDL (MIT).
- **Never copy AGPL code** — Sockseek and slskd are re-implemented from documented behaviour only, and slskd is used as a separate bundled process over HTTP (ADR-0004).

## How decisions work

Product decisions get a dated entry in [`docs/DECISIONS.md`](docs/DECISIONS.md); architectural ones get an ADR in [`docs/adr/`](docs/adr/), starting from `0000-template.md`. Both are binding: change them only with a new dated entry or ADR, never silently in code.

## AI-assisted development

Most implementation is delegated to cheap worker sessions driven by [`scripts/worker.py`](scripts/worker.py) (a tool-calling agent loop against the OpenRouter API), which work in a git worktree on a self-contained task file under `docs/build/tasks/` and have no design authority. The orchestrator session owns specs, review, verification and commits. If you are an AI session picking this up, read [`AGENTS.md`](AGENTS.md) and [`docs/build/AGENT_WORKFLOW.md`](docs/build/AGENT_WORKFLOW.md) first.
