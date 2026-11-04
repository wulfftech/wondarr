# Prompt: action the 2026-11-04 code and documentation review

Paste everything below the line into a Claude Code session running Claude Opus (`/model claude-opus-5-5`), opened in a clean checkout of `wulfftech/wondarr` with `main` merged up to the review branch, so that `docs/reviews/2026-11-04-code-and-docs-review.md` and `.claude/skills/writing-style/SKILL.md` are present.

---

You are the orchestrator for this repository, as ADR-0011 describes: Claude Code on Opus plans, delegates, reviews and merges; implementation runs in Sonnet subagents started with the Agent tool, each in its own git worktree; Haiku subagents do mechanical work and searches. Your job today is to work through `docs/reviews/2026-11-04-code-and-docs-review.md`, a review of the repository at commit `80f4a38` (release 0.1.16) with 141 findings: 8 high, 45 medium, 88 low, none critical.

Read these first, in this order, before you plan anything:

1. `docs/build/CODING_STANDARDS.md` and `CONTRIBUTING.md`. They are binding: one task is one branch (`<type>/<slug>`) and one reviewable diff; Conventional Commits in the imperative, scope in parentheses; never a model name or a session identifier in a commit message; docs move with code; green before done.
2. The review file's first sections ("How to use this file", "What was run", "About the writing-style review", "The short version", "Suggested order"), then the index table.
3. `CHANGELOG.md`, the entries for 0.1.14 to 0.1.16, to see the house style a changelog entry is written in.

## How to work

- **Follow the review's "Suggested order."** Each numbered group is one branch, delegated to one Sonnet worker in a worktree. Give the worker the finding blocks verbatim (id, summary, scenario, evidence, fix), the files involved, and the rules below. Do not hand a worker the whole report.
- **Verify before fixing.** Line numbers in the report are from `80f4a38` and will have drifted. A worker re-reads the quoted evidence against the current file and reproduces the scenario in a test before changing anything. A finding that does not reproduce is not fixed: it is reported back as `rejected: <reason>` with the evidence, and you judge it.
- **Fix what the finding names, nothing wider.** No refactors, no renames for taste, no new features. Where a finding names a test gap (`category: test-gap`, or a "no test covers" sentence in the fix), the fix includes that test, written as a sentence in the style of the existing tests (`Rejects_live_version_when_song_is_studio`).
- **Green before you review.** Every worker runs `dotnet build -warnaserror && dotnet test` and, when it touched `frontend/`, `cd frontend && npm run lint && npm run typecheck && npm test && npm run format`. When `.worker/test-host.env` or `WONDARR_TEST_HOST` is set, run them through `scripts/remote-run.sh` so this machine stays free. Nine Core tests are gated on ffmpeg: they need the image's ffmpeg 9.0.2 and a non-root user, and they skip when ffmpeg is not on PATH (findings `tests-01` and `tests-02` make that explicit; fix those two first if your box has a distro ffmpeg, so later runs are honest).
- **Review every diff yourself** before merging: read it against the finding, check the test fails without the fix, and check that no file outside the finding's scope changed. Merge with `scripts/safe-merge.sh <branch> "Merge <branch>: <what it does>"` (or `scripts/wsl-safe-merge.sh` on the WSL box), which refuses a red tree.
- **Docs move with code.** A fix that changes behaviour described in `docs/architecture/*.md` or on the website (`website/src/content/docs/**`) updates that text in the same branch. A user-visible fix gets a `CHANGELOG.md` entry under `[Unreleased]`, in the house style of the recent entries: what the user saw, why, what happens now, and `No migration.` when that is true.
- **You alone write to the review file.** Workers never edit `docs/reviews/2026-11-04-code-and-docs-review.md`. After each merge you record the outcome under the finding's heading in that file: `Outcome: fixed in <commit>`, `Outcome: rejected: <reason>`, or `Outcome: deferred: <reason>`. Commit the report update on `main` after each merge so parallel branches never conflict on it.

## Rules that need judgement, not just a fix

- **Decisions are not changed silently.** Some findings touch documented decisions: `ops-01` (which gates move a release tag), `api-02` (what `auth: none` promises), `core-search-05` (whether pushed releases obey the slot limit), `docs-internal-06`, `docs-internal-15` and `docs-internal-25` (ADRs that describe things never built). For each of these, do not pick a side in code. Write the options in one short paragraph each, put them in a "Questions for the owner" section at the end of the review file, and move on. If the owner has answered in the session, record the answer as an ADR amendment (the ADR's Amendments list) in the same branch as the fix.
- **Reference libraries are the user's own files.** Nothing in `src/Wondarr.Core/References/` may write, move or delete a file under a reference library root except the adopter's copy. `core-data-01` to `core-data-04` are database fixes; a worker that proposes touching the user's files is wrong.
- **Security findings get the smallest fix.** `api-01` wants the hash work capped, not the request; `api-02` wants the host rule applied to the `None` scheme, not a new authentication mode; `api-03` wants a clamp. Each gets a test that sends the exact request the finding quotes.
- **Style findings follow the skill in the repository.** The writing style is a project skill at `.claude/skills/writing-style/SKILL.md`, invoked as `/writing-style`. Load it before any documentation branch. Its rules 1 to 8 and every fact finding (S1 to S4) can be fixed straight away. Rules 9 to 14 are marked provisional in that file until the owner replaces its body with the original skill's text: while the file still carries its "reconstructed" note, do not fix or reject a finding tagged R9 to R14; give each `Outcome: deferred: style rule unconfirmed` and list the rule ids once under "Questions for the owner". Once the note is gone, compare the skill's rules with the appendix of the review file, fix the findings whose rule the skill holds, and strike the rest (`Outcome: rejected: not a rule in /writing-style`). Documentation branches are `docs/<slug>` and are small: one file or one closely related set of files per branch.
- **Rewrites, not patches, for four documents.** `docs/build/REPO_LAYOUT.md`, `docs/architecture/CONFIG_EXAMPLES.md`, ARCHITECTURE.md §5.4 to §5.6 and `.env.example` have drifted far enough that the fix is to regenerate them from the code (`git ls-files`, the options classes, the model snapshot, the controllers, `ScheduledTaskCatalog.cs`) and then read the result against the findings. Give those to one worker each with that instruction, and have the worker list in its done-report every claim it removed.

## Done means

- Every finding in the review file carries an `Outcome:` line.
- `main` is green by `scripts/safe-merge.sh --check --frontend`.
- The end of the review file has two sections you wrote: "Summary" (counts of fixed, rejected and deferred by severity, with the rejected ids and their one-line reasons) and "Questions for the owner" (the decision findings above and anything else a worker could not settle).
- No commit message names a model or a session, and no worker merged anything itself.

Work group by group; report to the owner after the high findings (groups 1 to 3 in the suggested order) with the summary so far, then continue unless told otherwise.
