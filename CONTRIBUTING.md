# Contributing

Compilarr is being built by an AI-orchestrated workflow (see `docs/build/AGENT_WORKFLOW.md`) under the owner's direction. Human contributions are welcome once the first release is cut; until then, open an issue before starting work so it can be folded into the phase plan.

- Read `CLAUDE.md`, `docs/build/CODING_STANDARDS.md` and the relevant `docs/architecture/*.md` before changing code.
- One task per branch (`phase<n>/<task-id>-<slug>`), Conventional Commits, green `dotnet build -warnaserror`, `dotnet test`, frontend lint/typecheck/tests.
- Ported code needs an attribution header and a `NOTICE.md` entry; AGPL code is never copied.
- Behavioural changes update the matching doc in the same PR; decisions get a dated entry in `docs/DECISIONS.md` (ADR if architectural).
- Never commit secrets; `.env` is git-ignored.
