---
name: writing-style
description: The writing style guide for everything written in or around this repository. Three styles in one document. Default (Style 1) is dry, punchy prose for .md files, READMEs, docs, store listings and UI copy, plus WHY-only code comments, and applies proactively to any of those. Write like me (Style 2) is a 1 to 5 formality dial for emails, Teams or chat messages, tone analysis and casual technical outreach such as GitHub issues to maintainers. Robust (Style 3) is a ban list of AI-slop words and constructions with a revision procedure, used when asked to de-slop or as a last pass. Load it before writing or editing prose or code comments, drafting an email or chat message, or reviewing prose for missteps.
---

# Writing style guide

Three writing styles in one document. They cover prose (`.md` files, READMEs, docs, store listings, UI copy), email and chat drafts, and code comments. Pick one style, apply it, and leave the other two alone unless the request combines them. Throughout, "the user" means the person whose voice the writing is in.

| Style | What it is |
|---|---|
| **Default** (Style 1) | Dry, punchy, confident prose. WHY-only code comments. Profanity on unless switched off. |
| **Write like me** (Style 2) | One voice on a 1–5 formality/humour dial — unhinged README up to formal client email — plus the email/chat drafting workflow and a casual technical outreach register. |
| **Robust** (Style 3) | Plain, specific, committed prose. A ban list of AI-slop words, tropes and stock constructions, judged by density, with a revision procedure. |

## Picking a style

1. Named in the request → that one.
2. Email, Teams/chat message, tone analysis, "write like me", a formality level, or a casual GitHub issue/reply or forum post to a maintainer/stranger → Write like me (the last one uses its Casual Technical Outreach register, not a numbered level).
3. "robust", "de-slop", "strip the AI tells", "less generic" → Robust.
4. Anything else in scope — `.md` files, code comments, user-facing copy → Default, applied proactively without being asked.

Code comments follow the comment rules in Style 1 whichever style is active. Write like me carries the same rules.

## Combining

Robust can run as a last pass over the other two ("write like me, then a robust pass"). The base style owns voice, humour and profanity. Robust only strips defaults, and it judges by density — the Default style's deliberate fragments stay unless they pile up.

## Profanity

Profanity is on by default. "No profanity" (or "no swearing") in the request switches it off. Each style says what that changes. Write like me levels 4–5 never swear, toggle or not.

## Applying a style

A request can carry a style name, target file(s), "no profanity", and a formality level 1–5.

1. Pick the style (above).
2. Open with a one-paragraph reminder of that style — the condensed version, not the whole section.
3. Read the target file(s). None given → ask what to target.
4. Rewrite prose and comments to match. For Robust, run the revision procedure in Style 3.
5. Report what changed in 1–2 sentences — the headline, not every edit.

Email and chat drafts follow Style 2's Email / Chat Workflow instead. Drafts only — never sent.

Used as a sub-step of a larger task (a summary file written at the end of a longer workflow, say): apply the named style to whatever that task writes, and skip steps 2, 3 and 5.

---

## Style 1 — Default

Dry, punchy, confident. Covers prose (`.md` files, READMEs, store listings, user-facing copy) and code comments. Profanity is on by default; "no profanity" turns it off.

---

### Prose — .md files and user-facing copy

**Voice:** Dry, confident, matter-of-fact. Knows exactly what it is and doesn't apologise for it.

**Structural rules:**
- Short sentences. Often fragments. "Nobody told you. That's not an accident."
- State what things do — don't rank fallbacks as lesser options
- No hedging. No "not this, but this" framing. No caveats upfront.
- Tables for technical lists (supported sites, permissions, options) — clean professional presentation alongside casual prose
- Ricky's weirdly-accurate summary line: "That's basically programming." / "so you're not sitting there like an idiot"
- Julian's structure: looks professional from a distance, wink underneath (both from Trailer Park Boys)
- Wry corporate observations: "Retailers aren't required to make this obvious. They've chosen not to."

**With profanity on (default):**
- "shit", "crap", "bullshit" are fine — occasionally, not every sentence
- Save the big ones for the opening where the problem is being set up: "Australia has 2 main problems — gambling ads and FUCKING MARKETPLACES."
- Don't layer swears — one per paragraph max

**With profanity off:**
- Same tone, same brevity, same wit — just drop the swears
- The dry observation still lands without them: "Retailers update their site structure constantly." is already deadpan
- Don't soften the sentences — just remove the words, keep the rhythm

**What to avoid:**
- Hedging: "may", "might", "could potentially", "in some cases"
- Forward-caveating: "Note that...", "Keep in mind...", "Please be aware..."
- Explaining what's obvious from context
- Over-long intros before the actual point

**Examples:**
```
✅  "Somewhere along the way, Big W became a marketplace. So did Bunnings."
✅  "You go to buy a Weber barbecue from Bunnings and half the results are from a seller
    called 'FitnessDirect888' shipping from a warehouse you've never heard of."
✅  "...profit"
✅  "That's basically how this gets maintained."
✅  "About 42% of their GMV is third-party, so statistically you're probably fine."

❌  "Please note that some retailers may update their selector structures over time."
❌  "This extension is designed to help users identify third-party marketplace sellers."
```

---

### Code comments

**Core rule:** Comment the WHY. Never the WHAT. If the function name already says it, shut up.

**Format rules:**
- Lowercase comments by default — not sentences, not title case
- One line per comment. No comment blocks. No docstrings.
- Section headers use the em-dash ruler pattern: `// ── section name ─────────────────`
- Inline comments go on the line they explain, not above it where avoidable

**WHY comments must be specific:**
- Name the site, the bug, the constraint, the workaround
- "// the iconic puts data-track-affiliation on the card div itself" — not "// check the element type"
- "// big w: /womens-clothing/c/6923100102 doesn't match anything in listingPatterns" — not "// fallback for edge cases"
- "// already clicked it, don't click again" — not "// prevent duplicate clicks"

**Strategy labels are fine for multi-path logic:**
```js
// strategy a — card hiding (primary)
// strategy a1 — native button (secondary)
// strategy c — highlight mode
```

**Inline trailing comments for constants and defaults:**
```js
let filterMode = "toggle"; // "toggle" = remove/button, "highlight" = purple overlay
const DIMMED_CLASS = "is-dimmed"; // visible but faded — used during peek
```

**Examples:**
```js
✅  // debounce because mutationobserver fires a lot during renders
✅  // spa navigation check — pathname + search so ?query=x changes trigger a rerun
✅  // use querySelectorAll + find, not querySelector
//  kogan has multiple p.font-body-low-emphasis — first one is often "42mm", not "Sold by"
✅  // don't run while the user is peeking at hidden items
//  mutationobserver and react re-renders will keep firing — this stops them
//  from re-hiding cards mid-peek
✅  // shadow DOM check — woolworths wc-product-tile renders seller info inside a shadow root
//  normal querySelector/textContent on the host element can't reach it

❌  // This function checks if the URL has changed since last navigation
❌  // Returns true if the card is a marketplace card
❌  // Handle the case where the element might not exist
```

**With profanity off:** No change. Comments don't swear either way — the tone is dry and direct, not sweary.

---

## Style 2 — Write like me

One voice, one dial. Instead of switching between a "professional" persona and a "sweary README" persona, everything the user writes sits somewhere on a single formality/humor scale. Pick the level from context, or let the user override it.

Covers prose, emails, Teams chat, README/docs, user-facing copy, code comments, and tone analysis of past messages. Level 2 is the same voice as the Default style (Style 1).

### When this style applies

The Default style (Style 1) handles the proactive cases — `.md` files, code comments and user-facing copy written without a style being asked for. Use this style when:
- drafting an email or a Teams/chat message
- asked for tone analysis
- asked to "write like me", or a formality level is named
- it's named explicitly
- drafting a casual GitHub issue/reply, forum post, or other short technical message to someone outside your own circle (an OSS maintainer, a stranger on a forum) — see Casual Technical Outreach below, a separate register from the numbered dial

### The Formality/Humor Scale

| Level | Name | Where it's used |
|---|---|---|
| 1 | Bender/TPB unhinged | Personal READMEs, popup copy, store listings, just-for-fun docs |
| 2 | Sweary-dry | Repo docs, technical READMEs, dry commentary with an edge |
| 3 | Casual chat | Teams/Slack messages, quick internal replies |
| 4 | Friendly-professional | Internal emails to close colleagues, low-stakes status updates |
| 5 | Formal | Exec/client emails, announcements, anything external or high-stakes |

**Default inference (no explicit level given):**
- Code comments, inline docs → Level 2 (never lower — comments stay dry, not chaotic)
- README / store listing / popup / error copy → Level 1
- Teams / instant message → Level 3
- Email to a named recipient → infer from past messages with that recipient (see Email / Chat Workflow); default to Level 4 internal / Level 5 external if no history exists
- If genuinely ambiguous, ask once rather than guessing

#### Level 1 — Bender/TPB unhinged
- Nihilistic, dry, confident. Think Futurama's Bender blended with Ricky and Julian (Trailer Park Boys).
- Short, punchy sentences. Fragments are fine. "Nobody told you. That's not an accident."
- Ricky-style overconfident-but-accurate observations: "it's basically a system," "that's basically programming," "so you're not sitting there like an idiot."
- Julian-style clean structure with a wink underneath — looks professional from a distance.
- No hedging ("may," "might," "could potentially") and no forward-caveating ("Note that...", "Please be aware...").
- Don't rank fallback options as lesser — just state what they do.
- Profanity (when toggle is on): "shit," "crap," "bullshit" — fine, not every sentence, one big swear max per section for emphasis at the setup of a problem.

#### Level 2 — Sweary-dry
- Same dry confidence as Level 1, slightly more restrained — this is documentation, not a rant.
- Tables for technical/structured lists (supported options, permissions, settings).
- Wry corporate observations allowed: "Retailers aren't required to make this obvious. They've chosen not to."
- Profanity (when toggle is on): occasional, one per paragraph max, never stacked.
- This is also the **code comment level** — see Code Comment Rules below (comments don't go below this level of dryness even at Level 1 projects).

#### Level 3 — Casual chat (Teams/IM)
- Friendly, down-to-earth, warm. Confident without arrogance.
- Short, conversational, often fragments. Minimal punctuation/capitalization in rapid exchanges ("ok done," "yep will do").
- Contractions and shorthand ("I've," "im," "ive") are fine in quick back-and-forth.
- Multiple short chat bubbles beat one long paragraph.
- Common phrases: "no worries," "no dramas," "all good," "mate," "heya." Verbal tics to open replies: "ah," "yeah," "yep."
- Light humour and emojis (🙂, 😊) welcome; gentle, never sarcastic or negative.
- Direct, frank questions ("which dates, mate?"); restate to confirm understanding.
- No signature, no formal sign-off — end with "cheers," "thanks," or just stop.
- Profanity: generally off regardless of global toggle — this is real workplace chat, not a repo doc. Only allow if the toggle is explicitly on AND the user confirms this recipient/context tolerates it.

#### Level 4 — Friendly-professional
- Bridge between casual chat and formal email: internal emails to close colleagues, low-stakes updates.
- Full sentences, proper grammar, but contractions still fine ("I'm," "there's").
- Can open with "Hi [Name]," and close with "Cheers," or "Thanks," before an optional short signature (name only, no full title block needed).
- Get to the point quickly; use bullets for multi-item updates.
- No profanity, regardless of toggle.

#### Level 5 — Formal (exec/client/external)
- Polite, professional, personable — not stiff. Confident without sounding corporate-robotic.
- Open with "Hi [Name]," / "Hello Team," rather than "Dear Sir." Acknowledge/thank early ("Thanks for the detailed summary...").
- Complete, grammatically clean sentences; fewer contractions ("I am" over "I'm"); spell out acronyms on first use for a broad audience, e.g. "RFU-P (Ready for Use – Permanent solution)."
- Structure multi-point updates with paragraphs, bullets, or numbered lists.
- Soften requests/instructions: "Could you please...", "Please let me know if...". Never command bluntly.
- Brief, genuine apologies only when actually at fault ("sorry for the delay") — never stack apologetic phrases.
- Close with "Kind Regards," + name (+ title/signature block on first contact or formal announcements). "Cheers" before "Kind Regards" is acceptable only for well-known internal colleagues, never external/client.
- No profanity, no slang ("mate," "lol"), no more than one restrained emoji if the relationship is very familiar — otherwise none, regardless of toggle.

### Profanity Toggle

- Default: **On**.
- Toggle only meaningfully affects Levels 1–2 (docs/code/comments). At Level 3 it defaults off unless explicitly confirmed for that audience. At Levels 4–5 profanity is **always off** — this is a hard safety rule, not a style preference, since a swear in a client/exec email or announcement can't be un-sent.
- When off: keep the same rhythm and dryness, just drop the words. "Retailers update their site structure constantly." already lands deadpan without a swear.

### Universal Don'ts (all levels except where politeness requires softened phrasing at Level 5)

- No hedging on facts: avoid "may," "might," "could potentially," "in some cases" unless genuinely uncertain.
- No forward-caveating: avoid "Note that...", "Keep in mind...", "Please be aware..." as throat-clearing before the actual point.
- Don't explain what's already obvious from context.
- Don't over-long the intro before getting to the point, at any level.

### Code Comment Rules (all levels — comments never drop below Level 2 dryness)

- Comment the **why**, never the what. If the function name already says it, don't add a comment.
- Lowercase, not sentences. One line per comment. No comment blocks, no docstrings unless the language/project convention requires them.
- Section headers use the em-dash ruler: `// ── section name ─────────────────`
- Inline comments go on the line they explain.
- Name the specific site/bug/constraint/workaround, not a generic label:
  - ✅ `// big w: /womens-clothing/c/6923100102 doesn't match anything in listingPatterns`
  - ❌ `// fallback for edge cases`
- Strategy labels are fine for multi-path logic: `// strategy a — card hiding (primary)`
- Inline trailing comments for constants/defaults: `const DIMMED_CLASS = "is-dimmed"; // visible but faded — used during peek`
- Profanity toggle off → no change; code comments are dry, not sweary either way.

### Casual Technical Outreach (GitHub issues, maintainer replies, forum posts)

A separate register, not a number on the formality/humor dial above. Use it for short casual messages to people **outside your own circle** — a GitHub issue or reply to an open-source maintainer, a forum post — where the content is technical but the relationship is a stranger, not a colleague or client. It doesn't replace Level 3 (Teams/IM with people you know) or the Email/Chat Workflow's tone-matched inference below, and it doesn't leak into the default `.md`-file prose voice (READMEs, docs) — apply it only when the request is specifically this kind of outreach message, not by default.

**Triggers:** "write this GitHub reply/issue casually", "draft a reply to this maintainer", "informal outreach", or any request to post on someone else's repo/forum in a chatty, off-the-cuff register.

**Open with a greeting and a handoff, not a summary.** Start with "Hey" or "Hey mate, here you go:" — not a diagnostic lead sentence like "Hardware confirmation: converted three units." An address term ("mate") is fine but goes once, right at the top, and never repeats.

**Write as "I," never "we" — even when the underlying work was collaborative or spanned multiple devices.** This register speaks as one person: "I," "my," "mine," never "we," "our," "ours." It's easy to default to "we" when describing a team result or a batch of devices tested together — that default is wrong here regardless.

**Tighten parenthetical lead-ins.** Drop filler words ("about," "separately") when a parenthetical can just state the thing plainly: "about keeping the swap inside the package instead of replacing the table separately:" becomes "(keeping the swap inside the package instead of replacing the table):".

**Don't restate a conclusion you already landed.** Each paragraph should add new ground, not re-summarize the one before it. If a sentence just recaps what the previous paragraph already established before pivoting to a caveat, cut the recap and go straight to the caveat.

**Don't give the recipient unsolicited advice.** Don't tell them what they should try next ("worth trying directly," "you should..."). End on your own next move or status instead — forward-looking about yourself, not instructive toward them.

**Strip internal project jargon when writing externally.** Evidence-grading notation, internal shorthand, any convention of the user's own research notes (e.g. "(E4, saw it three times)") has no place in outward-facing text. This register should carry zero trace of internal methodology vocabulary.

```
❌  On your follow-up about keeping the swap inside the package instead of replacing
    the table separately: checked the real manifest schema for this. `pt` doesn't
    carry a `min_version` field at all — only `boot` does (ours was `1.0.2`). So
    there's nothing for the installer to compare against on `pt` the way there is
    for the bootloader. On all three of our conversions it wrote PT every single
    time a package included one (`Installing PT 4096@fs_1(0x660000) -> pt(0x8000)...`),
    same as the other parts, unconditionally as far as we saw.

    That said I can't actually test the revert leg of your idea — send the original
    vendor bundle back afterward to restore Shelly's real table — our bench spare is
    already fully converted and the other two are deployed units we're not going to
    risk on an experiment. So "pt gets rewritten every time" is solid (E4, saw it
    three times), but "sending the original bundle back afterward actually restores
    a working Shelly table and boots it" is untested by us. If you've got a dev board
    spare it's worth trying directly — nothing in the schema should stop it.

✅  On your follow-up (keeping the swap inside the package instead of replacing the
    table): I checked the manifest schema- pt doesn't carry a min_version field at
    all - only boot does (mine was 1.0.2) so there's nothing for the installer to
    compare against on pt the way there is for the bootloader. all three conversions
    wrote PT every single time a package included one (Installing PT
    4096@fs_1(0x660000) -> pt(0x8000)...), same as the other parts, unconditionally
    as it seems.

    That said I can't currently test the revert leg (send the original img back to
    restore Shelly's table), my bench spare is already fully converted and the
    other two are deployed. I might be able to get another fresh one though.
```

**Drop bold section headers in favour of paragraph breaks.** No `**1. Hardware results**`-style numbered headers. Structure comes from blank lines between paragraphs, plus the occasional plain (non-bolded) lead-in word and a dash: "Bootloader check— small correction: ...". Tables are still fine for genuinely tabular data (device info, version numbers) — it's bolded headers over prose that go, not tables.

```
❌  **1. Hardware confirmation.** Converted a 1PM Gen3 and two 2PM Gen3 units — all three
    deployed, cases closed, no UART — via `Shelly.Update?url=`, through the slot-0/
    restore-first path this tool already uses.

✅  Hey mate, here you go:

    Device info (Shelly.GetDeviceInfo, read before conversion on each): [table]

    Stock version before, on all three was 2.0.1 (fw_id 20260923-075739/2.0.1-ge1a198b
    on the 1PM, 20260923-075759/2.0.1-ge1a198b on the 2PMs — same build, different
    timestamp).
```

**Compress jargon-y noun phrases into a plain verb clause.** Say what it lets someone do, not what category of thing it is:
```
❌  "a slot-flexible conversion mode"
✅  "a way to skip the restore step"

❌  "Slot-flexible conversion — no restore round-trip."
✅  "script handles slots itself"
```

**Drop apostrophes on contractions — inconsistently.** Not a hard rule either way: in the same message "didnt" and "hadnt" can lose the apostrophe while "you've got" keeps it. Mix it, the way someone typing fast actually does. Don't normalize to always-drop or always-keep, and don't touch every contraction in one message — leave some alone. Dash/hyphen spacing is just as inconsistent and not worth enforcing either — "schema- pt" (glued) and "at all - only" (spaced) can both appear in the same message without it reading as a mistake.

**Let exactly one real typo stand.** Not a dropped apostrophe — an actual spelling slip, like "beginnign" for "beginning". One per message of this length, left uncorrected, not cleaned up in a later pass. Don't add more than one — it reads as careless rather than casual.

**Trail lists off loosely.** "no warnings, errors etc" beats a clean parallel list ("no warnings, no retries, no errors"). "etc" as a casual trail-off is expected here, not a sign of a dropped thought.

**Never loosen the substance.** Exact fw_id strings, filenames (`appck.py`), function/method names (`bootswap.verify_image()`), algorithm notation (`0xEF ^ XOR(segment payloads)`) all survive untouched. Informality lives in the greeting, the structure, and the connective tissue between facts — never in the technical facts, numbers, or code identifiers themselves.

**Full example (the target for this register):**
```
Hey mate, here you go:

Device info (Shelly.GetDeviceInfo, read before conversion on each):
[table: unit/app/model/mac]

Stock version before, on all three was 2.0.1 (fw_id 20260923-075739/2.0.1-ge1a198b on
the 1PM, 20260923-075759/2.0.1-ge1a198b on the 2PMs — same build, different timestamp).
The 1PM was originally dumped at 1.4.4 for the static analysis bit, then updated to
2.0.1 before the live conversion, so 2.0.1 is the version that actually got converted
on all three.

After: ESPHome 2026.9.0, confirmed from the boot log (ESPHome version 2026.9.0 compiled
on 2026-10-14) and an esphome upload afterward that reset the uptime sensor

Bootloader check— small correction: I didnt run your ota.py directly, but built tooling
against the same mechanism (I hadnt found your repo at the beginnign). appck.py
recomputes the same 0xEF ^ XOR(segment payloads) byte + appended SHA256 you've got in
bootswap.verify_image(), validated OK against 2 original images. All three installs
went straight through with no warnings, errors etc.
```

### Email / Chat Workflow

1. **Gather context** — recipient(s), purpose, key points. If any are missing, ask (max 3 clarifying questions).
2. **Tone analysis** — if past messages are available (a mail/chat search tool, or samples the user pastes), pull 3–5 recent sent messages/emails to the same recipient (or similar recipients). Identify their established greeting, structure, sign-off, and formality patterns. This tone match takes priority over the default level inference above.
   - If no history is available, fall back to the default level inference (internal → Level 4, external/exec/client → Level 5, Teams → Level 3).
3. **Pick the level** — from message history if available, else the default inference table, else ask.
4. **Draft** — apply that level's rules from the scale above (greeting, tone, structure, sign-off, profanity).
5. **Output** — present the draft for review with a one-line note on the level/tone applied. Iterate on request.
6. **Never send** — only draft. The user reviews it and sends it themselves.

### Quick Examples

- "write this README like me" → Level 1, profanity on (default)
- "write this README like me, no swearing" → Level 1, profanity off
- "draft an email to [exec] about the outage" → Email workflow, history-inferred level (likely 5)
- "write like me but keep it level 3" → explicit override of inferred level
- "comment this function" → Level 2 code comment rules, regardless of surrounding project's overall level
- "draft a reply to this GitHub issue, keep it casual" → Casual Technical Outreach register, not a dial level

---

## Style 3 — Robust

Robust is plain, specific, committed prose. It names the thing, shows what follows from it, keeps every claim in proportion to its evidence, and picks the plain word over the important-sounding one. It sets no humour or profanity of its own.

- **Used alone:** plain, neutral register, with no added humour or swearing.
- **As a pass over another style** ("robust pass", "de-slop this"): that style's voice, humour, profanity and formality rules win. Robust strips the defaults below and leaves the voice alone.

### Judging a hit

#### Core principle

Every pattern here stands in for content: a rhythm that sounds meaningful, a word that announces importance, a phrase that would fit any subject. Three fixes cover nearly every hit: say plainly what is going on, show what it leads to, or delete it. People and models both produce these patterns by reaching for the most common phrase, and readers now spot many as machine tells.

#### Accumulation

- One instance is usually fine; density is the tell. Two close together deserve a look. Three in a row, the same shape in neighbouring paragraphs, or one move reused across subjects is a pattern: fix the pattern and leave lone instances that work.
- No witch-hunts. A lone em dash, "And" opener or fragment is not a finding, and em dashes aren't banned at all.
- The Default style (Style 1) uses fragments on purpose. Flag fragments, echo lines, conjunction openers and "Because" fragments only when they pile up.

#### Exemptions and conflicts

- **Exempt:** literal and technical senses (a test framework, a fluid layout, "navigate to Settings", raw data, an edge case), ordered steps ("Save, then restart"), quotes, names, code and error strings.
- **Character voice:** a phrase a character would genuinely say passes, used sparingly. Narration gets no such allowance.
- **Code comments:** apply the word clusters and the analyst-participle rule; the active style owns the format.
- **Conflicts:** specific beats short (in a terse style, swap in an exact word instead of adding a clause), clear beats smooth, and fit beats habit.

### Rules for any prose

Triggers match both -ise and -ize spellings (emphasise/emphasize). A synonym swap ("vital" → "essential") changes nothing; replace the word with the fact it stood in for, or cut it.

#### Word clusters

**AI vocabulary.** Use the specific word or fact; if there isn't one, cut.
- Nouns: tapestry, landscape (figurative), interplay, intricacies, nuance, dynamics, framework, paradigm
- Verbs: delve, foster, garner, underscore, showcase, emphasize, highlight and reflect (when editorialising), align, enhance, navigate (figurative), unpack (ideas, feelings)
- Adjectives: pivotal, crucial, vital, vibrant, intricate, nuanced, multifaceted, profound, compelling, poignant, evocative, visceral, palpable
- Adverbs: seemingly, arguably, notably, importantly, ultimately, fundamentally, inherently, undeniably, understandably

**Importance puffery.** Say what it does, or show the evidence: "plays a pivotal role in caching" → "caches responses for 60 seconds".
- stands as a testament to, a testament to, serves as a reminder of, enduring legacy, lasting legacy, lasting impact, indelible mark, deeply rooted, profound heritage, steadfast dedication, plays a vital/pivotal/crucial role, of paramount importance, cannot be overstated

**Promotional language.** Replace with a detail the reader could check. Literal geography passes.
- nestled, in the heart of, boasts a, stunning, breathtaking, continues to captivate, rich tapestry of, vibrant culture/community/scene, bustling, picturesque, idyllic

**Competence claims.** Say what it does and how to check it: "seamless sync" → "syncs on save". In fiction, show the movement.
- seamless, effortless, fluid, surgical precision, precision (as filler), with practiced ease/efficiency, meticulous, exacting, methodical, calculated, calibrated, clinical, deliberate and controlled

**Pseudo-analysis.** Name the actual structure, or cut.
- the architecture / geometry / calculus / mathematics / grammar of [anything abstract]

**Stock motion idioms.** Say plainly what happened.
- cut through the noise, sliced through, pierced through, wormed its way into, clawed its way up, fought its way to the surface, threatened to spill over, barely contained, held at bay

**Familiarity words.** Name the pattern ("the three pre-release checks") or delete the mention.
- familiar, usual, routine, ritual (unspecified), rhythm (meaning routine)

**Time-skip fillers.** If the time matters, say what filled it; if not, give the plain interval ("two weeks later") or move straight on.
- passed in a blur, flew by, time slipped away, before [you] knew it, lost track of time, the days blended together, time lost all meaning, [period] came and went

#### Constructions

| Pattern | Trigger | Fix |
|---|---|---|
| Negation formula | "not X, but Y" | Commit to Y and say it directly. |
| Negative parallelism | "not only… but", "it's not about X, it's about Y", "isn't just X, it's Y", "didn't just X. It Y'd." | State the point once, without the inflating setup. |
| Hedged label | "a [noun] that isn't quite/really a [noun]" | Name it: "a fix that isn't quite a fix" → "a workaround". Fiction: describe the gesture. |
| Analyst participle | ", highlighting / underscoring / reflecting / emphasizing / showcasing / symbolizing / illustrating / demonstrating Y" | State Y as a fact with its evidence, or cut it. Fiction: make the action carry Y, give Y to the character's thoughts, or trust the reader. |
| Trailing participles | ", [verb]ing…" tails on 3+ sentences running | Split, lead with the clause, or cut it. Check the actions really are simultaneous. |
| Simile as adverb | "with the [noun] of someone who…" | Describe the behaviour itself. |
| False range | "from X to Y" | Name a midpoint on the same scale. If you can't ("from setup to peace of mind"), list the real items or cut. |
| Echo line | consecutive sentences with mirrored structure | Cut the second unless it moves the thought on or pushes against the first; otherwise merge them. |
| Triple beat | three nouns or fragments for effect | Keep the one that matters and support it, or fold them into a sentence. Genuine lists of three are fine. |
| Staccato verbs | "[Verb]. [Verb]. [Verb]." | Write it as one sentence. Numbered steps are fine. |
| Conjunction openers | "And…", "But…" (density only) | Keep the ones that earn the break; join the rest to the previous sentence. |
| "Because" fragment | "Because X." alone (density only) | Attach it to what it explains, or show the cause. |
| Faux aphorism | a quotable line with no specific content | Cut it, or swap in the specific claim. Fiction: would this person think this, here? |
| Epic tone | "changed everything", "broke forever", "nothing would ever be the same", "the world held its breath" | Size the claim to the real consequence and state it: "this changes everything" → "v2 drops the build step". |
| Elegant variation | one referent cycling through synonyms or descriptors | One name per thing ("the extension"); switch only when the new term adds information. Fiction: a descriptor may mark POV, distance or a power change. |
| Message by proxy | "a look that said everything", "spoke volumes", "said it all", "didn't need words" | State what was communicated. |

Fiction-list metaphors used figuratively elsewhere ("sent ripples through the industry") take the same fix: say who changed what.

### Fiction and narrative only

Use with the rules above in stories and narrative passages. The lists are representative — treat close variants the same way.

#### Physical tells

- jaw tightens/clenches/sets/works, muscle ticks in jaw, teeth grind
- throat works, swallows hard, breath catches/hitches, exhales shakily, releases a breath they didn't know they were holding, forgets to breathe
- eyes darken, pupils blown, gaze hardens/softens, something flickers in their eyes
- hands curl into fists, knuckles whiten, fingers twitch, spine stiffens, shoulders tense, goes very still, freezes
- heart stutters/pounds, chest tightens, something cracks/loosens in the chest

**Ask:** What does this person alone do with the feeling (a habit, a speech change, a posture), and does it move the scene? Mention breathing only if it alters the pace or the power between people.

#### Interiority placeholders

- something in them shifts/breaks/cracks, something unnameable, something like [emotion], the weight of [X] settles, a wave of [emotion] washes over, [emotion] coils in the stomach, feels it in their bones, the air thickens, the room feels smaller
- Intensity: raw, primal, bone-/soul-/marrow-deep, paper-/gossamer-/razor-thin, threadbare, frayed edges, worn thin, stretched to breaking
- Change verbs with no change shown: shifted, settled, flickered, softened, hardened, sharpened, gentled, eased, tightened, loosened
- Realisation: clicked into place, the pieces slotted together, understanding dawned, clarity crashed over them, the truth of it settled, crystallized

**Ask:** What would someone in the room see, and what does the character feel or think? Name the emotion and what it makes them do or hold back. Show a realisation's reasoning.

#### Desire and restraint

- wanted him/her (no object), needed them like air, ached for, craved, "the need"
- held it together, swallowed it down, bottled it up, tamped down, held in check, barely leashed, white-knuckled control, iron grip on [emotion], shoved down, compartmentalized
- allowed/let themselves, gave themselves permission to, permitted themselves the luxury of, finally let go of

**Ask:** Wanted what, and why? What is held back, how, and what escapes when it fails? After a permission phrase, show what they then do.

#### Silence and suspended time

- the silence stretches/hangs/presses, hangs in the air, hangs between them, swallowed by the quiet, deafening silence, silence roared, time stretched, the moment crystallized, suspended in amber, the world narrowed to, everything else fell away
- for a long moment, for a beat, after a moment, a pause, then; "finally" as a beat; the silence that follows is [adjective]

**Ask:** What fills the gap, who ends it, and who pays for the wait?

#### Dialogue tags, voice and gaze

- said/asked + softly, quietly, carefully, finally, slowly, flatly, evenly, roughly
- voice drops/tightens/breaks/hardens/softens, pitched low, barely above a whisper, deceptively soft, falsely casual, too casual, guttural (try rough, torn, low)
- Gaze: assessing, appraising, cataloguing, watchful, guarded, shuttered, unreadable, inscrutable, too knowing

**Ask:** Delete the modifier; if the delivery is now unclear, rewrite the line or add an action. For a look, say what it conveys or hides.

#### Stock metaphors

- Texture: velvet, silk (over steel), steel, iron, granite, marble
- Temperature: cold voice or gaze, ice in the veins, blood ran cold, warmth spread through, heat pooled low, fire licked up the spine
- Impact: hit like a physical blow / punch to the gut / freight train, struck like a slap, knocked the air out of them
- Ripple: like a stone dropped in still water, rippled through, sent ripples through, the stillness shattered
- Breaking: cracked open, shattered something they didn't know was fragile, splintered, hairline fractures, fault lines, cracks in the foundation
- Anchor: anchored, tethered, grounded, moored, centered, rooted
- Edge: on the edge of something, teetering on the precipice, hanging by a thread
- Gravity: pulled toward, drawn to, orbits, the gravity between them, magnetic

**Ask:** Does the image help the reader understand? If not, describe the sensation, borrow a comparison from the character's own world, or cut. Write attraction as a choice.

#### Character shorthand

- Competence: graceful economy, efficient brutality, wasted no movement, quiet competence, easy confidence, contained power, coiled energy, deceptive stillness
- Menace: lethal grace, predatory stillness, coiled to strike, barely contained violence, quiet menace, the promise of violence
- Presence: commanded the room, filled the space, sucked the air out of the room, impossible to ignore
- Intimacy as invasion: closed the distance, breached their defenses, got under their skin, took root, settled into the cracks
- Possession: staked a claim, marked them, branded into, carved into their bones
- Damage as decoration: drink, sleeplessness or self-destruction as character colour

**Ask:** Which concrete act would lead a reader to that conclusion unprompted? Write it. For damage, show the price.

#### Scene mechanics

- ", then" between gestures ("He stands, then sits"). Does the order reveal anything? If not, make it one movement or a cause and effect.
- Mood-prop negation ("a cigarette he didn't want"). Why do they do it? Give a revealing reason, or cut it.
- Meta-narration ("the scene's not over", "the next beat"). Whose view is this? Get back inside the character through action.

#### Openings, set dressing and filler

- Atmospheric opener: weather, skyline or architecture before any character. If it would suit a film's establishing shot, cut it and open on someone acting or deciding.
- Wallpaper: light spills/pools, shadows play across, floor-to-ceiling windows, casting [X] in sharp relief, neon glow, ozone smell, the city hummed
- Looks: orbs/pools (eyes), alabaster/porcelain (skin), mane, sharp planes of his face, broad shoulders, lean muscle
- Filler: tees, sweats, leggings, soft cotton, sleeves rolled to the elbow; dishwashing, folding laundry, tea or coffee as filler, wiping counters

**Ask:** Does the detail cause trouble, reveal character or set up what's next? If not, cut it. Bring the setting in through what the character notices.

#### Dialogue clichés

- "You're such a menace / impossible / trouble / the worst / insufferable / ridiculous", "You're going to be the death of me", any line answered with "You love it."
- Oh God, Oh my God, God yes/no, Jesus, Christ, Lord

**Ask:** Would this person, with this history, say exactly this? If not, write what they would. Delete exclamations unless truly in character.

#### Endings

- And for now, that was enough. / It was a start. / They would figure it out. Somehow. / Everything had changed.
- Closing on stillness: "doesn't move", "still in hand", "doesn't say anything"

**Ask:** What does someone do or decide, and what has it changed? End on a small action or a line that alters the balance, or cut before the summary.

### Quick-scan

† = fiction and narrative only.

| Search | Action |
|---|---|
| "not X, but Y"; "not only… but"; "isn't just"; "not about X" | Commit; state it once |
| "isn't quite", "isn't really" | Name what it is |
| ", highlighting / underscoring / reflecting / showcasing" | State it as fact, or cut |
| ", [verb]ing" tails on consecutive sentences | Vary the structure |
| "with the [noun] of someone" | Describe the behaviour |
| "from X to Y" (abstract) | Check for a real scale |
| mirrored sentences; noun or verb triplets | Cut the echo, or fold into one sentence |
| clustered "And"/"But" openers, "Because X." fragments | Keep only the earned ones |
| rotating names for one thing; "the older man" | One name per thing |
| "changed everything", "broke forever"; quotable one-liners | Scale to the consequence; be specific |
| "spoke volumes", "said it all", "a look that said" | Say what was communicated |
| delve, tapestry, landscape, pivotal, crucial, vital, vibrant | Use the specific word or fact |
| testament, legacy, "plays a … role"; nestled, boasts, stunning | Say what it does; give a checkable detail |
| seamless, effortless, surgical precision, practiced ease | Say what it does (fiction: show the movement) |
| familiar, usual, routine, ritual, rhythm | Name the pattern, or cut |
| passed in a blur, flew by, slipped away, came and went | Show the time, or move on |
| ", then" between actions † | One movement, or cause and effect |
| "something" + verb; raw; "weight of"; "the need" † | Name the emotion or want |
| silence + stretches/hangs; "hangs in the air"; "for a moment/beat"; "finally" † | Show who ends the pause, and what follows |
| jaw, throat, hands + tension; breath + hitch/catch/release; "pupils blown" † | Give them their own tell, or delete |
| said/asked + adverb; carefully; voice drops/tightens; guttural † | Make the dialogue itself convey the tone |
| meets (bodies, mouths) † | Say who is doing what to whom |
| "held it together", "swallowed it down" † | Name what is held, and its cost |
| like a blow / punch / freight train; like a stone in water; rippled through † | Describe the sensation, or cut |
| steel, iron, velvet, silk; hot or cold for emotion † | Describe the sensation directly |
| grounded, anchored, tethered, rooted; pull, draw, orbit, magnetic † | Show it through action and choice |
| weather or skyline opener; "the city hummed"; busywork † | Open on a character; cut padding |
| Oh God, Jesus, Christ † | Delete |
| "And for now, that was enough"; "doesn't move" at the close † | Stop sooner, or close on an act or decision |

### Revision procedure

1. **Set the mode.** Standalone or a pass? In a pass, keep the other style's register, humour, profanity and deliberate fragments. Use the fiction section and † rows only for fiction or narrative.
2. **Before drafting** new text, collect the facts, numbers and names that will replace the defaults. Fiction: distinct tells per character, an opening on a character, a chosen closing act.
3. **While drafting**, stop at a trigger and ask its question then.
4. **Scan in phases**: constructions; word clusters; rhythm (count echo lines, triples, staccato, openers, fragments and trailing participles per paragraph); proportion (epic tone, aphorisms, puffery); naming; then the fiction groups if they apply.
5. **Judge each hit.** Literal, quoted or in character? How many nearby? Reused across subjects? Leave a lone instance that works.
6. **Fix** with the group's fix or question, in the active style's register and length.
7. **Final tests** on what remains:
   - Could you paste the line into another writer's piece without edits? Rewrite it until you couldn't.
   - Is the gesture or image generic enough to suit anyone? Tie it to this character or product.
   - What is different after this paragraph or scene? If nothing, finish it or cut it.
   - Does each detail tell the reader anything new? Remove the ones there only for looks.
   - Has what came before earned this claim or feeling? If not, add support or shrink the claim.
8. **Report** briefly: hits by pattern with rough counts, what changed, and what you kept on purpose (literal sense, lone instance, character voice).

---

Robust is adapted from "BANNED: The Definitive Guide", a personal anti-trope style guide for fiction (no author named in the copy used), condensed and reworded, with items split into any-prose and fiction-only. Its section on explicit sexual content is not included, and the fiction lists are representative, not the full catalogue.
