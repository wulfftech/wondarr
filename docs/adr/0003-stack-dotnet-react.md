# ADR-0003: Stack: C#/.NET 10, ASP.NET Core, EF Core + SQLite, React 19 + Vite; GPL-3.0

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner (alignment with the other *arrs)

## Context
Four stacks were compared (`docs/research/research_stack.md`). Python would reuse yt-dlp/mutagen/ytmusicapi/SoulSync directly; C# aligns with the *arrs, has ATL (MIT) for tagging, Quartz for scheduling, and turns the *arrs' GPL-3.0 code into a parts bin. With Soulseek handled by an external slskd process (ADR-0004), C# carries no Soulseek-library risk. The owner asked for alignment with the other *arrs.

## Decision
.NET 10 (LTS), ASP.NET Core controllers + OpenAPI + SignalR, EF Core 10 + SQLite (WAL) with checked-in migrations, hosted services + a SQLite-backed command queue + Quartz.NET, Microsoft DI, Serilog, ATL for tags (TagLib# fallback), `fpcalc` + AcoustID HTTP, YoutubeDLSharp for yt-dlp with Deno bundled, a small C# InnerTube client for YouTube Music search, typed `HttpClient`s with resilience and rate limiting. Frontend React 19 + TypeScript + Vite + TanStack Query/Table + Mantine, served from the same container. Licence GPL-3.0.

## Consequences
- Two languages (C# + TypeScript), as in every *arr.
- yt-dlp and YouTube Music are one wrapper further away than in Python; contract tests with recorded fixtures cover them.
- Python remains the documented runner-up if the C# path proves slower than expected.

## References
`docs/architecture/STACK.md` · `docs/research/research_stack.md`
