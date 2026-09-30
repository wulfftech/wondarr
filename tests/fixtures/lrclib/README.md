# LRCLIB fixtures

Real responses from `https://lrclib.net` recorded on 2026-09-30 with the User-Agent
`Wondarr/0.3 (https://github.com/wulfftech/wondarr)` (`docs/research/research_metadata_plex.md` §5.9).

| File | Request |
|---|---|
| `get-hit.json` | `GET /api/get?track_name=Get Lucky&artist_name=Daft Punk&duration=248` → 200 (synced and plain lyrics) |
| `get-miss.json` | `GET /api/get?track_name=Wondarr Fixture Nonexistent Song&artist_name=Nobody At All&duration=200` → 404 |
| `search.json` | `GET /api/search?track_name=Get Lucky&artist_name=Daft Punk` → 200, the first 5 of 20 records |
| `search-instrumental.json` | `GET /api/search?track_name=Aerodynamic&artist_name=Daft Punk` → 200, the first 4 of 20 records (the third is `instrumental: true` with no lyrics) |
