# Wondarr — TL;DR

Last updated: 2026-10-14

## What this is
A self-hosted *arr for music where the unit is one song (a MusicBrainz recording), not an album. You add songs, lists or playlists; Wondarr searches Soulseek first, YouTube Music Art Tracks second and torrents or usenet last, checks each download by length and AcoustID fingerprint, tags it and files it into a library layout that suits Plex and Plexamp. It also adopts the music folder you already have as a "reference library", so it doesn't download what you own. C#/.NET 10 and React 19, GPL-3.0, one Docker image on port 1077.

## Scope
Songs and the albums they need, from individual adds, pasted lists, Deezer and YouTube Music playlists, Spotify CSV exports, Last.fm and ListenBrainz. Sources: bundled slskd (or an external one), yt-dlp, qBittorrent and SABnzbd through Torznab, Newznab, Prowlarr or Gazelle. Out of scope for now: Transmission, Deluge, NZBGet, Jellyfin and Navidrome hooks, delay profiles (the "Later" list in `docs/build/PHASES.md`).

## Current state
- **Released and in daily use.** The latest release is 0.1.6 (2026-10-14); `ghcr.io/wulfftech/wondarr:0.1` and `:latest` point at it, built for amd64 and arm64.
- **Verified for each release:** unit and API tests (about 2,600 Core, 530 Api and 1,180 Sources tests), the frontend checks, a Docker smoke test that runs every phase gate (0 to 7) against recorded metadata, and a check on a live test instance before the tag.
- **Partial or unverified:**
  - The live 500-file reference-library check has not been run.
  - SABnzbd has only been tested against a fake.
  - How Plex treats moved music files (play counts, ratings, playlists) is undocumented; the owner accepted that risk.

## Work so far
- **2026-09-28 to 2026-10-09:** eight build phases took it from design to the 0.1.0 release: song identity, Soulseek, reference libraries, YouTube, upgrades, import lists, torrents and usenet, and polish.
- **2026-10-10 code review:** 94 findings, all fixed and shipped as 0.1.1 to 0.1.4. Email notifications came in 0.1.4.
- **2026-10-14, build session 13:** every open backlog item that needed no owner input was built, reviewed and shipped as 0.1.5 and 0.1.6. That included:
  - a "Download completed" notification event;
  - song searches that wait for a free download slot instead of giving up;
  - stricter matching on Various Artists folders, and lookups that prefer the original album recording;
  - two verification false negatives fixed;
  - .NET 10.0.13 for Microsoft's 2026-10-13 security advisories.

## Endgame
Done means a user can point Wondarr at their playlists and existing folder and get the right recording of every wanted song, at the quality their profile asks for, without wrong files and without re-downloading what they own. The remaining gaps are the live checks under "Outstanding" and the Later list.

## Outstanding
The cross-repo backlog (`agent-todo.json`, source `wondarr`) is the source of truth; 5 items are open:
- [ ] Run the live 500-file reference-library check with a folder of unedited album and single files (needs the owner's folder).
- [ ] Review the 80 files waiting in the test instance's Match queue; about 13 top candidates are wrong, so no blind bulk accept.
- [ ] A live SABnzbd check (needs a usenet provider and an NZB indexer).
- [ ] Two concurrent adds of different new songs by the same new artist can still collide on the artist record (2026-10-14-15).
- [ ] fyi: `ReferenceAdopter` duplicates part of `ImportService.LoadSongAsync`.

## How to carry on
- Instructions for agents are in `AGENTS.md` (git-ignored, in the main checkout). The build record is `docs/build/PROGRESS.md`; every decision and its reason is in `docs/DECISIONS.md`; `docs/HANDOVER.md` has the session log.
- Backend: `dotnet build -warnaserror`, then `dotnet test` per project in `tests/` (one at a time; parallel workers share a lock at `.worker/test.lock`).
- Frontend (`frontend/`): `npm ci`, then `npm run lint`, `npm run typecheck`, `npx vitest run --maxWorkers=2`, `npm run format`.
- Merging: `scripts/safe-merge.sh <branch>` builds and tests the current and the merged tree and refuses a red merge.
- Release: add a CHANGELOG section with an "Upgrading from …" note, update the README status line, commit `chore(release): x.y.z`, push an annotated tag `vX.Y.Z`. `release.yml` tests, smoke-tests both architectures, then moves `:0.1` and `:latest`.

## Risks and caveats
- **NuGet audit:** it fails the build on any newly published advisory, so a fresh restore can go red overnight with no code change (it did on 2026-10-13).
- **Downgrade:** from 0.1.5 on, going back to 0.1.4 is not supported until search runs with the new `NoFreeSlot` outcome age out (30 days).
- **Compact:** it moves and re-tags files to match the folder template, at most 250 per run in whole albums. Plex's behaviour on moved music files is unverified.
- **Gate replay:** the smoke test replays recorded metadata, so a change that makes new MusicBrainz, Deezer or iTunes requests needs a run of the "Record gate metadata" workflow and the new recordings committed.

## Decisions
The full list, with reasons, is in `docs/DECISIONS.md`. The ones that shape everything else:
- The unit is a MusicBrainz recording; albums are context for filing, not what gets searched.
- A download is accepted only after its length and AcoustID fingerprint confirm the recording; a wrong file is worse than a missing one.
- Reference-library files are never written, moved or deleted.
- Releases follow `:0.1`; a database migration always takes a backup first.
