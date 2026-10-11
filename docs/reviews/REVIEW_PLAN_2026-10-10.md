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

**Not planned.** Two findings already reached by known backlog items are left there:

- The Mirror delete with no busy check is the known "single song delete vs in-flight downloads" problem. Tranche 5 adds the guard on the Mirror caller only.
- The known compilation-ranking issue.
