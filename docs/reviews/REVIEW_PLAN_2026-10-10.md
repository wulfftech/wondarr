# Plan — fixing the 2026-10-10 code review

Source: [CODE_REVIEW_2026-10-10.md](CODE_REVIEW_2026-10-10.md) (94 findings: 11 High, 35 Medium, 48 Low; 35 owner questions).

## Assessment

The review is sound. I re-read the code behind R-001, R-005, R-006, R-008, R-010, R-011, R-040 and R-041, and each one is exactly as described. Most findings fall into four patterns:

- **A file moves, then the database write fails.** Recycle, placement, compaction, the mover and the converter can each leave the file in one place and the database pointing at another.
- **An `HttpClient` timeout is read as a shutdown.** It surfaces as a `TaskCanceledException`, and several loops catch that as "we are stopping".
- **Acting on things Wondarr does not own.** This covers torrents already in the client, a list read that came back empty or was cut short, and secrets sent to a URL the caller supplies.
- **The local-address bypass.** It checks one proxy header and never checks `Host`.

Nothing is Critical. The worst outcomes are a lost old file (R-001, R-002), an app crash loop (R-010), and the API key leaking past the local bypass (R-008, R-009).

Production runs MediaServer's qBittorrent and Prowlarr, so the torrent findings matter there, not only on Gazelle setups.

## Tranches

Each tranche is one or two M-tasks and follows the usual flow:

1. a spec;
2. a Sonnet subagent works in a worktree;
3. I run the tests;
4. a Sonnet review;
5. `safe-merge`;
6. PROGRESS, CHANGELOG and DECISIONS are updated.

Each fix comes with the test the review says is missing. The questions for each tranche are asked just before that tranche starts.

| # | Tranche | Findings | Owner questions |
|---|---|---|---|
| 1 | **Files and their records** (import, recycle, compaction, adoption) | R-001, R-002, R-012, R-013, R-047–R-051, R-003, R-004, R-015, R-018, R-072; test host writing to real `/data` paths | Q7, Q8, Q10, Q16, Q17, Q18 |
| 2 | **Timeouts and slskd supervision** | R-010, R-011, R-023, R-026, R-027, R-063–R-066 | Q33, Q34, Q35 (all have a default) |
| 3 | **Authentication and secrets** | R-008, R-009, R-036, R-037, R-038, R-054, R-055, R-058, R-059, R-090 | Q1–Q6 |
| 4 | **Torrents and usenet** | R-005, R-006, R-016, R-017, R-024, R-078–R-082 | Q25, Q26, Q27 |
| 5 | **Import lists and references** | R-007, R-019–R-022, R-074, R-076, R-014, R-073, R-077, Mirror delete busy-check | Q11–Q15, Q19 |
| 6 | **Search, metadata and the executor** | R-025, R-028–R-035, R-060, R-061, R-067–R-069, R-071, R-075 | Q20–Q22, Q24 |
| 7 | **Backup, release workflow and scripts** | R-039, R-040, R-044, R-045, R-052, R-053, R-056, R-057, R-091–R-094 | Q28–Q32 |
| 8 | **Front end** | R-041, R-042, R-043, R-086–R-089 | none |
| 9 | **Docs that disagree with code, and test hygiene** | R-046, R-062, R-070 (Q23); real-time sleeps, unbounded awaits, the silent symlink pass, tests that seed impossible states | R-046 (implement `%aunique` or fix the doc) |

**Order.** The order follows severity:

- Tranches 1 to 5 hold every High finding.
- Tranche 7's R-040 (the release workflow pushes `:0.1` before any test or smoke run) must land before the next release tag. Production follows `:0.1`.
- Tranche 8 is cheap and could run in parallel with any other tranche.

## Owner answers

**Tranche 1 (2026-10-11):**
- **Q8, choosing an album:** the file moves to the chosen album's folder and is retagged (M-13).
- **Q7, Plex's trash:** it is emptied only when that is safe (M-13). No verified Plex API lists the trash, so "safe" means all three of these:
  - the scan was seen to finish;
  - Plex's `autoEmptyTrash` setting is not on;
  - every folder of the section is confirmed present.
- **Q16 and Q17, failures that will never succeed:**
  - formats Wondarr cannot tag are rejected before grabbing, unless a conversion rule applies;
  - a rejected AcoustID key raises a Health error and fails imports after bounded deferrals (M-12).
- **Releases:** 0.1.1 ships after tranches 1, 2 and 7.
- **Defaults taken:**
  - Q18: `RecoverAsync` checks whether the file already landed (M-12).
  - Q10: sidecar matching by file stem stays as it is.

**Tranche 2 (2026-10-11):**
- **Q33, how long a stop may take:** 10 s, Docker's default. Raise the s6 grace and .NET's shutdown timeout to fit inside it; no compose change (M-14).
- **Q35, switching slskd mode:** the switch takes effect immediately, with no restart (M-14).
- **Q34, decided by me:** the "≤ 3 concurrent downloads" line in DEPLOYMENT.md means Wondarr's download slots; the doc is corrected to say so.

**Tranche 7 (2026-10-11):**
- **Q30, backups and upgrades:**
  - a database backup is written automatically before migrations, and a failed backup stops the upgrade;
  - a restore keeps 3 set-aside generations (M-15).
- **Q32, flaky-test re-runs:** they become policy, tightened. The re-run must account for every failure, theory cases included, and each flaky test goes to the backlog (M-15; DECISIONS entry at merge).
- **Q28, crediting ytmusicapi:** no. The InnerTube client stays recorded as protocol facts; no attribution is added.
- **Q31, VACUUM:** it is skipped while imports are running or queued, and writers wait up to 60 s for the lock (M-15).
- **Q29, the Sockseek "bracket check" citation:** no action. It credits a behaviour that was re-implemented, not copied, which ADR-0002 allows.

**Tranche 3 (2026-10-11):**
- **Q1, the default login rule:** keep `disabledForLocalAddresses` as the default, and harden it (M-16):
  - the bypass refuses any request that carries a proxy header;
  - it requires a LAN-style `Host`: a local IP, `localhost`, a single-label name, or `.local`/`.lan`/`.home.arpa`/`.internal`, plus a `server.allowed_hosts` list;
  - any other request is asked to log in.
- **Frame protection (R-059):** blocked by default with CSP `frame-ancestors 'self'`. A `server.frame_ancestors` list lets named dashboards embed Wondarr (M-16).
- **Q6, file modes:** every file that holds secrets is owner-only (0600) whatever `UMASK` is: `config.yml`, backups, `slskd.yml`, and the database with its `-wal` and `-shm` (M-17).
- **Defaults taken:**
  - Q2: download links use a short-lived signed token instead of `?apikey=` (M-16);
  - Q3: a stored secret is sent only to the host it was saved for (M-17);
  - Q4: secrets stay masked for unregistered types (M-17);
  - Q5: `/` and the data directory are refused as shared, download and library folders (M-17).

**Tranche 4 (2026-10-11):**
- **Q25, cancelling a torrent song:** Wondarr records which torrents it added (M-18).
  - A torrent it did not add is never removed and never has a file switched off.
  - Its own torrent is removed, with data, only when nothing has downloaded and qBittorrent is not checking or moving it.
- **Q26, the usenet job:** the code stays as it is. The job is deleted once its files are moved into staging. DECISIONS build session 8 #5 is corrected.
- **Q27, minimum seeders:** Prowlarr gets the same filter as Torznab (M-18).
- **R-082, passkeys in download links:** a link is kept only while a grab could still use it, then cleared, with a 24 h housekeeping backstop (M-18).

**Tranche 5 (2026-10-12):**
- **Q12, deleting a reference library:** the dialog states how many songs would become wanted and offers three choices (M-20):
  - make them wanted and search;
  - make them wanted without searching now (the default);
  - unmonitor them.
- **Q13, items that return to a list:** they are re-added and re-monitored, unless the user unmonitored the song themselves (M-19).
- **Q15, the first reference scan:** songs added by import lists wait, unsearched, while a reference library has unidentified rows. Manual adds still search (M-19).
- **Q19, free text in bulk:** a free-text line auto-resolves only when the hit's artist and title closely match what was typed. A bare title never auto-resolves (M-19).
- **Defaults taken:**
  - **Q11, missing reference files:** a song is released only after two scans and 24 h; a scan that finds more than 10 % (or 200) missing is treated as a mount problem and releases nothing; a moved file is followed (M-20).
  - **Q14, a changed file:** it keeps the user's Skipped or Adopted choice (M-20).

**Tranche 6 (2026-10-13):**
- **Q20, "Search now" above the cutoff:** automatic grabs, "Search now" included, stop once the cutoff is met. Interactive search still lets the user grab anything (M-23).
- **Defaults taken:**
  - Q21: push runs do not count toward the backoff (M-23).
  - Q22: the batch loops yield their executor place while they wait (M-23).
  - Q24: the spacing has a margin, MusicBrainz 1.05 s and AcoustID 350 ms (M-22).
- **Releases:** 0.1.2 shipped tranches 3–5. Production is updated after it.

**Not planned.** Two findings already reached by known backlog items are left there:

- The Mirror delete with no busy check is the known "single song delete vs in-flight downloads" problem. Tranche 5 adds the guard on the Mirror caller only.
- The known compilation-ranking issue.
