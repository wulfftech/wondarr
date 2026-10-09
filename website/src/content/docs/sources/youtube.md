---
title: YouTube Music
description: The optional YouTube Music source, which is off by default.
---

YouTube Music is the second source, after Soulseek. It runs only when Soulseek yields nothing acceptable for a song. It uses yt-dlp, and the image includes yt-dlp and the Deno runtime it needs to solve YouTube's challenges.

## It is off by default

Open **Settings → YouTube** and turn on **Enable the YouTube source** ("Off by default. Enabling it is your own choice, on your own egress."). The first time you do, a dialog titled "Before you enable YouTube" appears. It says YouTube's Terms of Service prohibit automated access, and that Wondarr bundles no account, cookies or token: you enable the source with your own credentials, on your own egress. Press **I understand** to continue, or **Keep it off**. The dialog is remembered in your browser and does not show again.

The **yt-dlp** card at the top shows whether yt-dlp and the Deno runtime answer. **Test** runs a fresh check.

## What it picks

Wondarr searches YouTube Music for the song and prefers **Art Tracks**, the label-supplied audio. **Allow videos results** is off by default; turning it on lets official videos be used when no Art Track is found, though the duration check still rejects the ones with intros and outros.

A YouTube stream is Opus at roughly 160 kbps, so a grab is ranked as `OPUS-160` whatever the file is converted to afterwards.

## Output

**Output policy (default)** controls what the downloaded audio is converted to: the codec (**aac**, **mp3** or keep the original Opus), CBR or VBR, the bitrate (kbps) and the sample rate. The default is AAC at 256 kbps constant bitrate with the sample rate kept. A library can override it.

## Settings

| Setting | Default | Variable |
|---|---|---|
| Enable the YouTube source | off | `APP__YOUTUBE__ENABLED` |
| Allow videos results | off | `APP__YOUTUBE__ALLOW_VIDEOS` |
| Search budget per song | 20 (1 to 50) | `APP__YOUTUBE__SEARCH_LIMIT` |
| Cookies file | none | `APP__YOUTUBE__COOKIES_PATH` |
| PO-token provider | none | `APP__YOUTUBE__PO_TOKEN_BASE_URL` |
| Seconds between requests | 0.75 | `APP__YOUTUBE__YTDLP__SLEEP_REQUESTS_SECONDS` |
| Sleep before each download | 10 | `APP__YOUTUBE__YTDLP__SLEEP_INTERVAL_SECONDS` |
| Maximum sleep | 20 | `APP__YOUTUBE__YTDLP__MAX_SLEEP_INTERVAL_SECONDS` |
| Retries | 5 | `APP__YOUTUBE__YTDLP__RETRIES` |

A setting taken from the environment is locked in the page.

- **Cookies file**: a Netscape cookie file yt-dlp reads, for age-gated videos (for example `/config/cookies.txt`). It is yours; Wondarr does not provide one.
- **PO-token provider**: the base URL of your own bgutil PO-token sidecar (for example `http://host:4416`), for when YouTube asks for a proof of origin. Optional.

yt-dlp downloads one file at a time, into `/data/downloads/youtube`.
