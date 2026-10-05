# yt-dlp fixtures

Canned stdout/stderr per scenario, served to `YtDlpRunner` through a fake `IProcessRunner` so the
tests never touch the network or a real yt-dlp. The error strings are the ones re-verified in
`docs/research/research_youtube.md` §0.1.

- `download-success.stdout` — a successful download; the last line is the `--print after_move:filepath` path.
- `download-webm.stdout` — a download that came back `.webm` (the remux did not run).
- `formats-success.stdout` / `formats-no-js.stdout` / `formats-no-js.stderr` — the `-F` probe.
- `bot-check.stderr`, `rate-limited-page.stderr`, `rate-limited-429.stderr`, `geo-restricted.stderr`,
  `age-gated.stderr`, `age-gated-login.stderr`, `private.stderr`, `unavailable.stderr`,
  `not-exist.stderr`, `garbage.stderr` — one taxonomy row each.
