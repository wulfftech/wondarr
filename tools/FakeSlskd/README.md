# FakeSlskd

A stand-in for the bundled slskd, for the CI gate only. CI cannot reach the Soulseek network, so the
Phase 2 gate runs the real image with this process in place of `slskd`: `SlskdHost` starts it exactly
as it starts the real binary, it reads the same `slskd.yml`, and it answers the slskd 0.26 API subset
Wondarr uses from a **scenario file** — including real encoded audio, generated with the image's
`ffmpeg`, fingerprinted with `fpcalc`, and served back by an AcoustID stub on a second port.

It is a test tool: never part of the image, never shipped.

## Why it is a whole process, not a test double

The gate has to prove things a mock cannot: that the app's own `SlskdHost` starts and supervises the
process it is configured with, that its API key from `slskd.yml` is accepted, that its searches come
back, that downloads land where the app expects them, and that the Soulseek search budget held. All of
that needs the real HTTP surface on the real port.

## Building it

```
dotnet publish tools/FakeSlskd -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o .fake-out
```

`<AssemblyName>` is `slskd`, so the published single file is `.fake-out/slskd`.

## Using it in the smoke test

1. Publish the tool and mount the published directory into the container next to the real binary.
2. Point the app at it and at the scenario; the AcoustID stub's base URL must end in `/v2/`:

```
APP__SOULSEEK__BINARY_PATH=/fake/slskd
FAKE_SLSKD_SCENARIO=/config/phase2-scenario.json
APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/
```

The fake also publishes `GET /fake/log` on its own port, loopback only and without an API key, for the
gate to read back what the app asked for: every search with its post and delete times, the highest
number of searches that were ever in flight, and every transfer with its destination and state.

## What it answers

| Endpoint | Notes |
|---|---|
| `GET /api/v0/application`, `/api/v0/server` | `version.current` `0.26.0`; logged in when `soulseek.username` is set; share counts from `shares.directories` |
| `GET /api/v0/shares` | one entry per shared directory |
| `POST /api/v0/searches` | 200 with the search, 429 when more than the scenario's `maxInFlight` are in flight |
| `GET /api/v0/searches`, `/searches/{id}`, `/searches/{id}/responses` | responses stay **empty** until the search completes, like the real one |
| `PUT`/`DELETE /api/v0/searches/{id}` | complete now / 204 then 404 |
| `POST /api/v0/transfers/downloads/batches` | 201 all queued, 207 some failed, 200 none; `options.destination` must be relative and must not traverse upwards |
| `GET /api/v0/transfers/downloads` (`/{username}/{id}`, `/{id}/position`) | users → directories → files |
| `DELETE /api/v0/transfers/downloads/{username}/{id}?remove=` | 204; a pending transfer becomes `Completed, Cancelled` |
| `GET /fake/log` | the gate's log (loopback only) |

Every `/api/v0` request needs `X-API-Key` matching one of `web.authentication.api_keys.*.key`, or gets
401 — unless `web.authentication.disabled` is true.

## Downloads

A scenario file's `audio` block is rendered by `ffmpeg` into `directories.incomplete` (in slskd's own
`<incomplete>/<username>/<remote path>` layout), then moved to
`<downloads>/<destination>/<bare file name>`; `options.destination` defaults to the remote path's last
directory segment, which is slskd's `${SOURCE_DIRECTORY}` default. `size` and `bytesTransferred` become
the file's real size, the state becomes `Completed, Succeeded`, and the fake POSTs
`DownloadFileComplete` to every webhook in `slskd.yml`'s `integrations.webhooks` that listens for it,
with that webhook's configured headers. Distinct `audio.seed` values give distinct fingerprints; a
pure sine would not, which is why the noise component is there (`tests/fixtures/media/README.md`).

`transfer` picks the peer's behaviour: `ok` (default), `reject` (`Completed, Rejected`) or `stall`
(`InProgress` for ever, nothing transferred).

## AcoustID stub

On `127.0.0.1:$FAKE_ACOUSTID_PORT` (default 5031) the fake answers `POST`/`GET /v2/lookup`. A
fingerprint it generated resolves to the recording in that scenario file's `identity` block, with a
score of 0.96; anything else returns `{"status":"ok","results":[]}`. An empty `client` returns
AcoustID's error code 4, an empty `fingerprint` code 3. Gzip-compressed form bodies are accepted.

## Tests

`tests/Wondarr.Sources.Tests/FakeSlskd/FakeSlskdTests.cs` hosts both apps in process through
`FakeSlskdApp.Build` / `AcoustIdStubApp.Build`, injecting an `IAudioGenerator` fake so the tests do not
need `ffmpeg`. The real `FfmpegAudioGenerator` is exercised only when `ffmpeg` and `fpcalc` are on
`PATH`; that test reports itself as skipped otherwise.

`tests/gate/phase2-scenario.schema.md` describes the scenario document.