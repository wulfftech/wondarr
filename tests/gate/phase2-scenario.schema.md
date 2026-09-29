# Phase 2 gate scenario

What `tools/FakeSlskd` offers the app as "the Soulseek network", and how it behaves while the app
downloads from it. The smoke test passes the file's path to the fake as `FAKE_SLSKD_SCENARIO`.

```jsonc
{
  // How long a submitted search stays InProgress (ms, default 1500).
  "searchDelayMs": 1500,

  // How long a queued transfer stays Queued, Remotely before it starts (ms, default 1000).
  "transferDelayMs": 1000,

  // How many searches may be in flight at once; a further POST answers 429 (default 2).
  "maxInFlight": 2,

  "files": [
    {
      "username": "gate-peer",              // the peer that offers the file
      "path": "@@gate\\Music\\Daft Punk\\Get Lucky.flac",  // the peer's own path, backslashes
      "size": 30038322,                     // bytes; omitted -> estimated from bitRate x length
      "bitRate": 320,                       // kbps; omit for lossless
      "sampleRate": 44100,                  // Hz; omit for lossy
      "bitDepth": 16,                       // omit for lossy
      "length": 249,                        // seconds
      "hasFreeUploadSlot": true,
      "uploadSpeed": 1146398,               // bytes/s
      "queueLength": 0,

      // "ok" (default) completes, "reject" ends Completed, Rejected, "stall" stays InProgress.
      "transfer": "ok",

      // How the "download" is produced; the file is real encoded audio. Distinct seeds give
      // distinct fingerprints, and a pure sine does NOT - see tests/fixtures/media/README.md.
      "audio": {
        "codec": "flac",                    // "mp3" or "flac"
        "bitrateKbps": 320,                 // mp3 only
        "durationSeconds": 249,
        "seed": 7,
        "frequency": 440
      },

      // What the AcoustID stub answers for that file's fingerprint.
      "identity": {
        "recordingId": "833f00e1-781f-4edd-90e4-e52712618862",
        "title": "Get Lucky",
        "artists": [{ "id": "056e4f3e-d505-4dad-8ec1-d04f521cbb56", "name": "Daft Punk" }],
        "durationSeconds": 249
      }
    }
  ]
}
```

## Matching

A scenario file is offered as a search response when **every token of the normalised search text
occurs among the normalised tokens of its `path`**. Normalisation lower-cases, folds Latin diacritics
(`Jóga` → `joga`), turns every non-alphanumeric into a separator and collapses runs of separators. So
`daft punk get lucky` matches the path above, while `daft punk get lucky remix` does not (extra token),
and neither does `get lucky daft` — token order does not matter.

Files are grouped into responses by `username`, so one peer is one response with
`hasFreeUploadSlot`, `uploadSpeed` and `queueLength` taken from its first matching entry.

## Notes

- A missing or unreadable scenario file is not an error: the fake then answers every search with no
  responses, which is how a "nothing found" gate case is expressed.
- `size` and `bitRate`/`sampleRate`/`bitDepth` are what the search response advertises; the transfer's
  `size` and `bytesTransferred` become the real file size once the audio is generated.
- `durationSeconds` of `audio` should match `length`, and `identity.durationSeconds` what the app
  expects to verify, or the app's own checks will reject the download — that is the point of the gate.