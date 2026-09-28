# Coding standards

These apply to humans and AI workers alike. Workers are told to read this file before every task.

## General

- **Small, reviewable changes.** One task → one branch/worktree → one PR-sized diff. Never mix refactors with features.
- **Build and tests must be green before a change is considered done.** `dotnet build -warnaserror` and `dotnet test` for the backend; `npm run lint && npm run typecheck && npm test` for the frontend.
- **No secrets in the repo.** Keys live in `.env` (git-ignored) or the app's `/config`. Tests use fixtures, never live credentials.
- **Attribution for ported code.** Any file adapted from Lidarr/Prowlarr/Sonarr (GPL-3.0), SoulSync or spotDL (MIT) carries a header comment naming the source repository, path and licence, and an entry in `NOTICE.md`. Sockseek (AGPL) is re-implemented from documented behaviour, never copied.
- **Conventional Commits** (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:`), imperative mood, scope in parentheses when useful (`feat(slskd): …`). No model names or AI-session identifiers in commit messages.
- **Docs move with code.** A change that alters behaviour described in `docs/architecture/*.md` updates that doc in the same change; a new decision gets a dated entry in `docs/DECISIONS.md` (and an ADR if architectural).

## C# (.NET 10)

- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, analyzers on (`Microsoft.CodeAnalysis.NetAnalyzers`, `StyleCop.Analyzers` or the .NET SDK analyzers with `AnalysisLevel=latest-recommended`).
- File-scoped namespaces; `record` types for DTOs, value objects and API resources; `sealed` by default; async all the way (`Task`/`ValueTask`, `CancellationToken` on every I/O method).
- Dependency injection through constructor parameters; register in one place per project (`ServiceCollectionExtensions`).
- External HTTP: typed `HttpClient`s via `IHttpClientFactory`, resilience with `Microsoft.Extensions.Http.Resilience` (retry with jitter, honour `Retry-After`), and a per-host rate limiter (MusicBrainz 1/s, AcoustID 3/s, LRCLIB 200–500 ms gaps, iTunes 20/min, Soulseek search budget 30 per 4 min).
- Persistence: EF Core with explicit migrations checked in; no lazy loading; queries in repositories/services, never in controllers.
- Time via `TimeProvider`; file system via an `IFileSystem`-style abstraction so tests do not touch disk.
- Logging: structured (`ILogger<T>`), never log secrets (API keys, Soulseek password, Plex token). Redaction filter in the Serilog pipeline.
- API: controllers under `/api/v1`, resources as records, paging `page/pageSize/sortKey/sortDirection`, errors as RFC 7807 problem details, OpenAPI generated and committed to `docs/api/openapi.json` on each release.

## Frontend (React 19 + TypeScript + Vite)

- `strict: true`; ESLint (typescript-eslint, react-hooks) + Prettier; no `any` without a comment.
- Data fetching only through TanStack Query hooks in `src/api/`; API types generated from `openapi.json` (`openapi-typescript`).
- Mantine components; no ad-hoc CSS beyond CSS modules for layout.
- Every page has a loading, empty and error state.

## Tests

- **Golden tests** for the parts that decide correctness: filename/path parsing, release-name quality parsing, candidate scoring, naming templates, album policy, tag mapping. Inputs and expected outputs live in `tests/fixtures/*.json` so workers can add cases without touching code.
- **Contract tests** for every external API client against recorded fixtures (`tests/fixtures/<service>/`), plus an opt-in live mode (`COMPILARR_LIVE_TESTS=1`) that is never run in CI.
- **Integration tests** for the API with `WebApplicationFactory` and an in-memory SQLite database.
- Unit tests use xUnit + FluentAssertions + NSubstitute. Test names read as sentences: `Rejects_live_version_when_song_is_studio`.

## Definition of done (per task)

1. Acceptance criteria in the task file are met and demonstrated (test or command output pasted in the done-report).
2. Build, tests, lint and typecheck green locally.
3. Docs updated where behaviour changed; `docs/build/PROGRESS.md` row updated.
4. Diff reviewed by the orchestrator (and, for risky areas, the reviewer agent) before merge.
