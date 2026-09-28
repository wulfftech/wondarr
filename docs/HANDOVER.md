# Handover — Compilarr

**Prepared:** 2026-09-28 · **By:** the planning session (Claude Code, cloud) · **For:** the first build session, run locally from `D:\Code\compilarr` with Claude Code on **Claude Opus 5.5** as orchestrator.

## 1. Where things stand

- Research and design are **complete and approved by the owner**. Every decision is in `docs/DECISIONS.md` (with ADRs in `docs/adr/`); the full planning record is `docs/PLAN.md`; the evidence is in `docs/research/`.
- **No application code exists yet.** The repository holds documentation, the worker runner (`scripts/worker.py`), Claude Code agent definitions and settings.
- The next unit of work is **Phase 0** (`docs/build/PHASE_0_TASKS.md`): repository skeleton, config, DB, auth, scheduler, SignalR, frontend shell, bundled slskd supervisor, Dockerfile/compose, CI. Its "done when" gate is in `docs/build/PHASES.md`.

## 2. The product in one paragraph

A self-hosted *arr whose unit is one **MusicBrainz recording**. Wanted songs are searched on Soulseek (bundled slskd) first, YouTube Music Art Tracks (yt-dlp) second, torrents/usenet (qBittorrent, SABnzbd) last; candidates are scored (identity, quality, availability, source preference), downloads are verified (ffprobe + duration + AcoustID), tagged (Picard mapping), and filed under a library layout (flat / artist / artist-album / **Plexamp** preset with a "fewest albums per artist" policy). Quality profiles with cutoff/upgrades (default cutoff 320). Inputs: individual adds, pasted lists, Deezer/YouTube Music playlists, Spotify CSV exports, Last.fm/ListenBrainz, and the user's existing music folder (reference library with a match queue). C#/.NET 10 + React 19, GPL-3.0, one Docker image, port 1077.

## 3. Start the next session (Windows, `D:\Code\compilarr`)

```powershell
git clone https://github.com/wulfftech/compilarr D:\Code\compilarr      # or: git -C D:\Code\compilarr pull
cd D:\Code\compilarr
copy .env.example .env      # then fill OPENROUTER_API_KEY and COMPILARR_WORKER_MODEL (see docs/build/AGENT_WORKFLOW.md §6)
```

Prerequisites: .NET 10 SDK, Node 22+, Python 3.10+ (for `scripts/worker.py`), Docker Desktop (WSL 2), Git, the `claude` CLI on `PATH`.

Then open Claude Code in the folder, select the orchestrator model (`/model claude-opus-5-5`), and paste the full kickoff prompt from `docs/build/NEXT_SESSION_PROMPT.md` (short form below):

> Read CLAUDE.md, docs/HANDOVER.md and docs/build/PROGRESS.md. Run the worker-model bake-off from docs/build/AGENT_WORKFLOW.md §6 (or skip it if COMPILARR_WORKER_MODEL is already pinned), then execute Phase 0 task by task using cheap workers via scripts/worker.py, reviewing every diff and keeping PROGRESS.md current. Stop at the Phase 0 gate and report.

Sanity check before delegating anything: `python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md` prints the exact command and environment (key redacted) that a worker would get.

## 4. Things the owner still has to provide (not blockers for Phase 0)

- An **AcoustID application key** (`acoustid.org/new-application`) for the verification pipeline (Phase 2 tests).
- A **dedicated Soulseek account** for the bundled slskd (one login per username; do not reuse a personal client's account).
- A **Plex token** and test library if Phase 3 should be validated against a real server.
- The pinned **worker model** after the bake-off, and a **phase budget** for worker spend.
- Optional: qBittorrent/SABnzbd/Prowlarr test instances for Phase 7.

## 5. What not to do (the short list; full list in `CLAUDE.md`)

Do not embed Soulseek.NET; do not copy AGPL code; do not fork Lidarr; do not write `.webm` or fake lossless; do not exceed the Soulseek search budget; do not put secrets or AI identifiers in commits; do not let workers edit decisions, ADRs, `CLAUDE.md`, `.claude/`, workflows or `.env*`; do not merge red.

## 6. Knowledge map (nothing from the planning session is lost)

| Topic | Where |
|---|---|
| Why Lidarr cannot do this; *arr internals to copy | `docs/research/FINDINGS.md` §3.1, `docs/research/research_arr.md` |
| slskd API/config/limits, Soulseek network rules, Sockseek/soularr matching internals | `docs/research/research_soulseek.md`, `docs/architecture/MATCHING_ENGINE.md` §6.4, `docs/architecture/DEPLOYMENT.md` §9.5 |
| yt-dlp 2026 (EJS/Deno, PO tokens, formats), ytmusicapi, spotDL scoring | `docs/research/research_youtube.md`, `MATCHING_ENGINE.md` |
| Torznab reality, qBittorrent/SABnzbd selective download, partial-seed mechanics, Lidarr quality parser | `docs/research/research_torrent_usenet.md`, `QUALITY_DEFINITIONS.md` |
| MusicBrainz/AcoustID/tag mapping/Plex scanner behaviour/naming tokens | `docs/research/research_metadata_plex.md`, `LIBRARY_OUTPUT.md` |
| Stack comparison, Soulseek.NET licence issue, code to port | `docs/research/research_stack.md`, `docs/architecture/STACK.md` |
| Existing projects (SoulSync, DroppedNeedle, …) and name collisions | `docs/research/FINDINGS.md` §3.6 |
| Every owner decision with rationale | `docs/DECISIONS.md`, `docs/adr/` |
| Data model, API, jobs | `docs/architecture/ARCHITECTURE.md` |
| Phases, gates, first tasks | `docs/build/PHASES.md`, `docs/build/PHASE_0_TASKS.md` |
| How to use cheap workers | `docs/build/AGENT_WORKFLOW.md`, `scripts/worker.py`, `.claude/agents/` |
