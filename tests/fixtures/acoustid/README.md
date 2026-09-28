# AcoustID fixtures

`lookup-invalid-key.json` is a **real** response (HTTP 400) recorded 2026-09-29 from `POST https://api.acoustid.org/v2/lookup` with an invalid client key.
`docs-example.fingerprint.txt` is the example fingerprint from acoustid.org/webservice (duration 641); `lookup-docs-example.json` is its **real** lookup (`meta=recordings`, 2026-09-29: M83, two AcoustIDs with score 1.0 for the same recording) and `lookup-tone-unknown.json` the **real** lookup of `tests/fixtures/media/tone-320.mp3`'s fingerprint. Note: a recording's `duration` is a **float** in seconds (`637.333`).

Every other `lookup-*.json` is **synthetic**, built from the documented response shape (`docs/research/research_metadata_plex.md` §2.3) because a real file of the wanted recordings was not at hand; their shape matches the real responses above. The MBID `833f00e1-781f-4edd-90e4-e52712618862` is the real Daft Punk "Get Lucky" album recording; the other recording ids are made up.

| File | HTTP | Meaning |
|---|---|---|
| `lookup-match.json` | 200 | score 0.962; recordings include the wanted MBID (and a second one) |
| `lookup-low-score.json` | 200 | wanted MBID, score 0.61 (needs review) |
| `lookup-other-recording.json` | 200 | score 0.95 for a different (live) recording only |
| `lookup-same-title-duplicate.json` | 200 | a different MBID with the same title, artist and duration (an MB duplicate) |
| `lookup-no-recordings.json` | 200 | a known fingerprint with no linked recordings |
| `lookup-empty.json` | 200 | fingerprint unknown to AcoustID |
| `lookup-rate-limited.json` | 429 | error code 14 |
| `lookup-invalid-fingerprint.json` | 400 | error code 3 |
