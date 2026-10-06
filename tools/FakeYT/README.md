# FakeYT

A stand-in yt-dlp for the Phase 4 gate, in the FakeSlskd spirit. CI cannot reach YouTube, so the
gate runs the real image with this script in place of the bundled yt-dlp:
`APP__YOUTUBE__YTDLP__BINARY_PATH` points at it and `FAKE_YT_SCENARIO` names the scenario file.

It answers the exact surface `YtDlpRunner` and `YtDlpAvailability` use:

| Invocation | Answer |
|---|---|
| `--version` | the scenario's `version` line (default `2026.08.19`) |
| `-F --no-progress -- <watch url>` | a formats listing with format rows, or the scenario's failure |
| the download flags | real encoded Opus (ffmpeg, pink noise + sine, distinct per video id) written into the `-o` template's directory, `after_move:filepath` printed, exit 0 — or exit 1 with the scenario's failure stderr |

The failure stderr strings are the exact ones `YtDlpErrorTaxonomy` matches, so the gate exercises
the real classification: a `bot-check` video fails the item with the retry-later reason (never a
re-grab in the same run), a `geo` video is blocklisted and the next candidate is tried.

A scenario file maps video ids to behaviours:

```jsonc
{
  "version": "2026.08.19",
  "videos": {
    "dQw4w9WgXcQ": { "kind": "ok", "durationSeconds": 213, "seed": 11, "frequency": 440 },
    "botVideo01":  { "kind": "bot-check" },
    "geoVideo01":  { "kind": "geo" }
  }
}
```

Kinds: `ok` (the fields above), `bot-check`, `rate-limited`, `geo`, `age-gated`, `private`,
`unavailable` (exit 1 with that taxonomy string). A video id the scenario does not name answers
`ok` with a 30 s file, so a scenario only describes its interesting videos.

It is a test tool: never part of the image, never shipped. The smoke test publishes it as a
single executable directory next to FakeSlskd and mounts it read-only, the same way.
