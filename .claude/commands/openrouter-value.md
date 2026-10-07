---
description: Refresh docs/build/MODEL_VALUE_MATRIX.md from live OpenRouter prices, discounts and benchmark scores, and report which tier picks changed
argument-hint: "[--top N] [--code-floor X] [--intel-floor X] [--agent-floor X] [--include-batch]"
allowed-tools: Bash(python scripts/openrouter_value.py:*), Bash(git diff:*), Bash(git status:*)
---

Run `python scripts/openrouter_value.py --out docs/build/MODEL_VALUE_MATRIX.md $ARGUMENTS` from the repo root. It pulls live data from the OpenRouter API (no key needed; `openrouter.ai` must be reachable) and rewrites the matrix.

Then:
1. Show the "Tier picks" table (T1 mechanical, T2 standard worker, T3 hard / reviewer, plus the informational Orchestrator row).
2. Run `git diff docs/build/MODEL_VALUE_MATRIX.md` and say which tier picks changed, which discounts appeared or expired, and which picks only win because of a large discount (compare "Value" with "List value").
3. If a pick changed for a task that is already running or specified, say so; otherwise just use the new picks from here on.
4. Do not commit unless I ask, or unless this is the session's pre-flight refresh (then include it in the session's first push as `docs(build): refresh the model value matrix`).
