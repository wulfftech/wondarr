# ListenBrainz fixtures (recorded 2026-10-07, public data, no token)

- `feedback-loved-p0.json` — `GET /1/feedback/user/mr_monkey/get-feedback?score=1&count=3&offset=0&metadata=true`: `count` 3, `total_count` 244, `feedback[].recording_mbid` plus `track_metadata` (`track_name`, `artist_name`, `mbid_mapping`). Page with `offset`/`count`.
- `user-playlists.json` — `GET /1/user/mr_monkey/playlists?count=2`: `playlist_count` 31; each `playlists[].playlist` is JSPF without tracks (`identifier` is `https://listenbrainz.org/playlist/{mbid}`).
- `playlist-34c1bb9f.json` — `GET /1/playlist/34c1bb9f-ef5f-4b5a-9305-1be299e86bb0`, trimmed to its first 3 of 26 tracks: JSPF `playlist.track[]` with `title`, `creator`, `album`, `duration` (ms) and `identifier` (recording URL(s) `https://musicbrainz.org/recording/{mbid}`).
- `browser-check.html` — what the API answered (HTTP **200**, `text/html`) to a request made a few seconds after the others: an anti-bot "Verifying your browser" page. A client must treat a non-JSON answer as "try later", never as an empty list.

Rate limit headers seen: `X-RateLimit-Limit: 30`, `X-RateLimit-Remaining`, `X-RateLimit-Reset-In` (seconds).
