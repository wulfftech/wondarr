---
name: writing-style
description: The house style for every piece of prose in the Wondarr repository, including the README, CONTRIBUTING, the CHANGELOG, the website pages, the architecture and build documents, ADRs, commit subjects and bodies, pull request descriptions, UI copy and code comments. Load it before writing or editing any of them, and when reviewing prose for missteps.
---

# Writing style

> **Status: reconstructed on 2026-11-04.** The owner's original `/writing-style` skill lives on their own machine and could not be read from the cloud session that wrote this file. The rules below were reconstructed from the repository's own most recent prose: CHANGELOG entries 0.1.9 to 0.1.16, the website pages under `website/src/content/docs/`, and recent commit subjects. Rules 1 to 8 are the house style as practised and can be applied with confidence. Rules 9 to 14 are the conventions such a skill usually carries; treat them as provisional until this note is gone. **Owner: replace everything below the front matter with the original skill's text and delete this note.**

## Who reads this prose

Someone running Wondarr at home who wants to know what happens to their songs and files, or a contributor who wants to know what the code does. Neither wants to be sold anything or to decode jargon. Write so that the newest changelog entry and the oldest architecture page sound like the same person.

## Rules

1. **Plain words, full sentences, present tense.** Say what happens to the user's thing: the song, the file, the indexer, the scan. Never "functionality", "leverage", "robust", "seamless", "streamline", "ensure", "utilise", "powerful", "simply", "just", "easily", "note that", "please note", "it is worth noting". Say "will" only for a real future; the product's behaviour is described in the present.
2. **British spelling:** honour, licence (the noun), behaviour, catalogue, recognise, organise, colour, centre. Identifiers and quoted UI labels keep whatever spelling the code uses.
3. **Numbers carry a unit, with a space:** 100 ms, 98 %, 5 seconds, 24 hours, 320 kbps. No bare number standing in for a unit, no "~" for "about".
4. **UI labels are bold, exactly as the UI shows them,** and a navigation path joins bold labels with an arrow: **Settings → General → Contribute with feedback**. Paths, keys, environment variables, commands and file names are in code font: `config.yml`, `APP__SERVER__URL_BASE`, `/data/music`.
5. **Name the actor.** "Wondarr", "the scan", "the import", "slskd" do things. Avoid agentless passives that hide who acts ("is handled", "will be processed") and avoid "the system".
6. **No marketing, no filler, no exclamation marks, no rhetorical questions** in reference material. The README's product voice may be warmer, but it is still spelled correctly and says concrete things.
7. **"for example" and "say", not "e.g.", "i.e.", "etc." or "and so on"** in finished prose.
8. **Spell names the way their owners do:** slskd, yt-dlp, MusicBrainz, AcoustID, ffmpeg, ffprobe, fpcalc, SABnzbd, qBittorrent, Plexamp, GHCR, Last.fm, ListenBrainz. Check spelling and grammar before you finish.

Provisional, until the owner confirms them:

9. **No em-dashes or en-dashes in running prose.** Use a comma, a colon or a full stop. The dash between a version and a date in a changelog heading (`## [0.1.16] — 2026-11-03`) is an established convention and stays.
10. **No arrows and no "X → Y → Z" chains in prose,** except the navigation paths of rule 4.
11. **No parenthetical that carries a whole clause,** no nested parentheses, no stacked asides. Make it its own sentence.
12. **No abbreviation the reader has not been given:** MB, LB, CAA, YT, MBID, CBR, VBR. Write "MusicBrainz id", "constant or variable bitrate". No section references ("§7.3", "section 6") that point at documents outside the repository.
13. **Headings are noun phrases.** Lists are parallel. A bullet is one or two sentences, not a paragraph. Bold is for a lead phrase or a UI label, never a whole sentence.
14. **No "it's not X, it's Y" framing, no triplets for rhythm, no "Let's", no "we" addressing the reader** in reference material, no emoji.

## Shapes the repository uses

**A commit subject** is `type(scope): what changes, in plain words`, in the imperative, under about 72 characters, with the Conventional Commits types from `docs/build/CODING_STANDARDS.md`. It names the behaviour, not the mechanism: `fix(references): decide a link again when the song's file changed under it`. A merge commit reads `Merge <branch>: <what it does>`. Never a model name or a session identifier.

**A changelog entry** under `### Added`, `### Changed` or `### Fixed` opens with a bold lead phrase that states the outcome for the user, then says what the user saw before, why it happened, and what happens now, and ends with `No migration.` when that is true. Each release has a one-paragraph summary under its heading, then an **Upgrading from x.y.z** block that says whether there is a database migration or a new setting. Look at the entries for 0.1.14 to 0.1.16 before writing one.

**A website page** has a front-matter `title` and `description`, explains one thing, names every setting key and UI label exactly, and links to the other page rather than repeating it.

**An architecture or build document** states what the code does today. Anything designed but not built is marked as such or left out. Every class, option key, command name, table and endpoint it names exists in the tree.

## Before you finish

- Read the text once as the user and once as the contributor.
- Grep the tree for every path, key, label, command and version you named.
- Run the rules as a checklist: 1 to 8 always, 9 to 14 while they stand.
