# Task <ID>: <short imperative title>

**Phase:** <n> · **Depends on:** <task ids or none> · **Suggested tier:** cheap worker | single-shot API | orchestrator-only · **Estimated size:** S/M/L

## Goal
<One paragraph: what exists after this task and why it matters.>

## Deliverables
- <file or component 1>
- <file or component 2>

## Allowed paths
- `src/Compilarr.Core/...`
- `tests/Compilarr.Core.Tests/...`
(Anything else is out of scope; do not touch it.)

## Context to read first
- `docs/architecture/<doc>.md` §<section>
- `<existing file>` lines <a–b>
- Fixtures: `tests/fixtures/<...>`

## Constraints
- <library/approach constraints, ported-code sources and licence, performance/rate limits, naming>

## Acceptance criteria (each must be demonstrated)
1. `<command>` → `<expected result line>`
2. <behavioural criterion with a concrete example>
3. Tests added: <what they cover>; golden fixtures added under `tests/fixtures/...` if applicable.

## Out of scope
- <explicitly excluded things>

## Done-report
Use the format in `docs/build/WORKER_SYSTEM_PROMPT.md`.
