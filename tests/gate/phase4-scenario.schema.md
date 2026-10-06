# Phase 4 gate scenario

What `tools/FakeYT` (the stand-in yt-dlp) answers for each video, and what the gate expects the app
to do with it. `scripts/phase4-scenario.py` writes the file; the smoke test passes its path as
`FAKE_YT_SCENARIO`.

```jsonc
{
  // The version FakeYT's --version probe prints.
  "version": "2026.08.19",

  // The gate's expectations, keyed by song title (the gate looks the songs up by title).
  "songs": {
    "Get Lucky": {
      "expect": "art-track",       // "art-track" | "omv-rejected" | "bot-check"
      "source": "youtube",
      "videoId": "4D7u5KF7SP8",    // the Art Track the fake InnerTube fixture offered
      "omvVideoId": "..."          // the OMV candidate, for the omv-rejected case
    }
  },

  // What FakeYT answers per video id. An id the scenario does not name answers "ok" with a 30 s file.
  "videos": {
    "4D7u5KF7SP8": {
      "kind": "ok",                // "ok" | "bot-check" | "rate-limited" | "geo" | "age-gated" | "private" | "unavailable"
      "durationSeconds": 370,      // the file's real length
      "seed": 100,                 // distinct seeds give distinct fingerprints
      "frequency": 440,

      // What the AcoustID stub should answer for this file's fingerprint: the recording the app's
      // verification resolves it to. FakeYT registers it with the stub right after generating the
      // file (FAKE_ACOUSTID_REGISTER points at the stub's /v2/register).
      "identity": {
        "recordingId": "833f00e1-781f-4edd-90e4-e52712618862",
        "title": "Get Lucky",
        "artists": [{ "id": "", "name": "Daft Punk" }],
        "durationSeconds": 370
      }
    },
    "botVideo01": { "kind": "bot-check" }
  }
}
```

## The InnerTube side

The app's InnerTube client is pointed at FakeSlskd's InnerTube stub
(`APP__YOUTUBE__BASE_URL=http://127.0.0.1:5032/`), which answers from the recorded fixtures under
`tests/fixtures/ytmusic/`:

- an ISRC-shaped query (two letters, five digits, seven more characters) → `search-isrc.json`
  (the a-ha "Take On Me" card — the verified result an ISRC query returns),
- the songs `params` → `search-songs.json` (six Art Track results, the first at 6:10),
- the videos `params` → `search-videos.json` (the first result a UGC re-upload titled "Official
  Video" at 4:08 — the official-video hazard),
- anything else → the songs fixture.

The stub records every query it was asked for in the fake's `/fake/log` (as `innertubeSearches`),
which is how the gate proves the tier ordering: a song Soulseek filled never reaches InnerTube.

## The gate's three checks (`scripts/phase4-gate.py`)

1. **Art-Track fills**: every `art-track` song has a file, imported from YouTube, with fingerprint
   verification recorded — within one MissingSearch cycle.
2. **OMV rejected by duration**: the `omv-rejected` song's history holds a rejection whose reason
   names the duration, and the import came from the Art Track, not the long video.
3. **Bot check backs off**: the `bot-check` song's item failed with the bot-check reason, the fake's
   log shows exactly one grab for it per run, and a second round (after the backoff) tries once more
   — never a loop.
