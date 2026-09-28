# Test fixtures

Recorded inputs and expected outputs live here, so workers can add cases without touching code
(`docs/build/CODING_STANDARDS.md` § Tests).

## Layout

```
fixtures/
├── <golden suite>/          # e.g. naming/, quality/, scoring/, album-policy/, mapping/
│   └── *.json               # { "cases": [ { "input": …, "expected": … } ] }
├── <service>/               # recorded HTTP responses, one directory per external API
│   └── *.json / *.xml       # slskd, qbittorrent, sabnzbd, musicbrainz, acoustid, …
└── media/                   # tiny real files: sample audio, .torrent, .nzb
```

## Conventions

- **Golden tests** — one JSON file per suite; every case is a named `input`/`expected` pair so a new
  case is a data-only change. Files are UTF-8, LF line endings, two-space indent.
- **Contract tests** — the recorded response verbatim, plus a sibling `.meta.json` recording the
  request URL, status, and the date it was captured. Never edit a recording by hand; re-capture it.
  Recordings must not contain credentials, session cookies or personal data.
- **Live mode** — contract tests run against recordings by default and only hit the network when
  `COMPILARR_LIVE_TESTS=1` is set. Live mode never runs in CI.
- **Media files** — keep them tiny (a fraction of a second of silence, a one-file torrent). Anything
  larger than a few hundred kilobytes does not belong in git.
- Fixtures are read with a path relative to the test assembly's output directory, so every fixture
  file must be copied to output (`CopyToOutputDirectory`); do not hard-code absolute paths.