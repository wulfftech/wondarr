# ADR-0013: Discover: song radio from ListenBrainz, Last.fm and Deezer, with previews

**Status:** Accepted · **Date:** 2026-11-17 · **Deciders:** owner, orchestrator session

## Context
The owner wants a Discover tab that suggests songs one at a time, the way a Spotify radio does, where each suggestion can be previewed and added to the library. Spotify's recommendation and related-artist endpoints closed to new apps on 2024-11-27, so they are out. Three sources remain, all checked on 2026-11-17:

| Source | Seed | Key | What comes back |
|---|---|---|---|
| ListenBrainz Labs `GET labs.api.listenbrainz.org/similar-recordings/json?recording_mbids=<mbid>&algorithm=<name>` | recording MBID | none | recording MBID, name, artist credit, release MBID, CAA id, score; heavy on the seed's own album |
| Last.fm `track.getSimilar` | artist + title, or MBID | Wondarr's Last.fm key (already a setting) | artist, title, match; MBIDs usually empty |
| Deezer `GET /artist/{id}/radio` | the seed's Deezer artist | none | Deezer tracks with a `preview`; no ISRC without a `/track/{id}` call |

The ListenBrainz algorithm parameter is an enumeration that its server validates, and the names change. Wondarr has no play history of its own. The owner wants the default mix drawn from the library as a whole, not from recent additions or scrobbles.

## Decision
Discover is a page and a `GET /api/v1/discover` endpoint. It runs in two modes:
- **Radio:** seeded by one song.
- **Mix:** the default when the page opens. It is seeded by a random sample of the library's songs, and Refresh draws a new sample.

Each source sits behind an `IRecommendationSource`. ListenBrainz is always on. Last.fm runs only when its key is set. Deezer runs only when the seed has a Deezer id. Suggestions are processed in this order:
1. **Merge.** Suggestions are merged on recording MBID, then ISRC, then normalised artist and title. Each source's rank is turned into a score.
2. **Filter.** These are dropped: what any library or reference library already holds, the seed itself, and anything the user dismissed.
3. **Cap.** A radio holds at most three songs per artist.

A suggestion is resolved to a MusicBrainz recording only when the user adds it, through the existing add-song path, so browsing costs MusicBrainz nothing. Previews use the existing preview endpoint, which looks the URL up at play time and never stores it: by Deezer id, then ISRC, then a Deezer artist-and-title search. Results are cached per seed for six hours. Dismissals are stored, and each one can be undone.

The ListenBrainz algorithm name is a setting with a current default. If the server rejects it, that source is skipped with a warning naming the accepted values.

## Consequences
- Discover works with no keys at all. Last.fm adds per-track similarity for users who already set a key.
- No MusicBrainz request is made until Add. External calls follow the existing limits: ListenBrainz at 1 request/s honouring its `X-RateLimit-*` headers and 429, Deezer through its quota handler, and Last.fm through its gate.
- The gate replay gains a ListenBrainz upstream. The Last.fm and ListenBrainz base URLs become settings so that they can be recorded.
- Deezer artist radio is similarity per artist, not per track. It is scored below the two per-track sources.

## References
`docs/architecture/ARCHITECTURE.md` · https://labs.api.listenbrainz.org/ · https://www.last.fm/api/show/track.getSimilar · https://developer.spotify.com/blog/2024-11-27-changes-to-the-web-api
