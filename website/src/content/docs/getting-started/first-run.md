---
title: First run
description: Signing in, the first library, quality profiles and adding your first songs.
---

Open `http://192.168.1.10:1077` (your host's address). The navigation on the left has Library, Add songs, Wanted, Match, Activity, Settings, System, Tasks, Backup and Logs.

## Signing in

Authentication is set in the `server` section of `/config/config.yml`:

| Key | Values | Default |
|---|---|---|
| `auth` | `forms`, `none`, `external` | `forms` |
| `auth_required` | `enabled`, `disabledForLocalAddresses` | `disabledForLocalAddresses` |

- `forms` is a login page with a username and password stored by Wondarr.
- `none` turns authentication off. Only use it on a network you trust.
- `external` leaves authentication to a reverse proxy in front of Wondarr.
- With `auth_required: disabledForLocalAddresses`, callers on your local network (loopback, link-local and the private ranges `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) skip the login. A request that carries an `X-Forwarded-For` header is never treated as local, so behind a reverse proxy everyone logs in. Set `auth_required: enabled` to require a login from every address.

Both can be set from the environment: `APP__SERVER__AUTH` and `APP__SERVER__AUTH_REQUIRED`.

There is no page in the web UI for creating the login yet. Set the username and password through the API, using the API key from `config.yml` (the password must be at least 8 characters, the username at most 64):

```bash
curl -X PUT http://192.168.1.10:1077/api/v1/auth/user \
  -H "X-Api-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{"username": "wondarr", "password": "a-long-password"}'
```

Until you do, the login page says no credentials are configured. See [Install](/wondarr/getting-started/install/) for where the API key is.

## Your first library

A library called **Music** exists from the start: root path `/data/music`, layout **Plexamp**. Change it under **Settings → Library**. The fields are:

- **Name** and **Root path**: the folder inside the container where songs are filed.
- **Layout**: **Flat**, **Artist**, **Artist → Album** or **Plexamp**.
- **Naming template**: the path each song is filed under. The page shows a preview, a **Tokens** helper, and a button that resets it to the layout's preset. The Plexamp preset is `{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}`.
- **Album policy**: how a stand-alone song gets an album. The default, **Fewest albums**, uses the fewest real albums per artist and puts the rest in a Singles album. The others are **Singles only**, **Original album** and **Single release**. **Minimum tracks per real album** (default 2) applies to Fewest albums.
- **Plex music section** and **Library folder as Plex sees it**: see [Plex](/wondarr/sources/plex/).

The Plexamp preset keeps an album folder level on purpose. Plex groups music by folder and tags, and the preset makes the album tags deliberate so Plex does not split or rename albums.

**Add library…** creates another library. **Convert existing files…** and **Compact library…** are for files already in a library.

## Quality profiles

Two profiles are seeded, under **Settings → Quality profiles**:

- **Standard 320**: the cutoff is MP3-320. AAC-256 counts as meeting it. FLAC is allowed, upgrades are on.
- **Lossless**: the cutoff is FLAC. High-lossy files (MP3-320, MP3-VBR-V0, AAC-320) are accepted as a stop-gap, and Wondarr keeps looking for lossless.

A song is wanted until a file meets the profile's cutoff. Below the cutoff, Wondarr keeps searching for an upgrade.

## Adding the first songs

Go to **Add songs**. It has three tabs.

- **Search**: type `Artist - Title`, a MusicBrainz or Deezer link, or an ISRC. Choose the right recording from the candidates and press **Add**. A monitored song is searched for straight away (`search.search_on_add`, on by default).
- **Album**: search for an album (`Artist - Album`), pick a release and add songs from its tracklist.
- **Paste a list**: one `Artist - Title` per line, up to 1000 lines. Pick the **Quality profile** and press **Add all**.

Watch progress under **Activity → Queue**, and finished and failed grabs under **History**. Songs still waiting for a file are under **Wanted**.

Before Wondarr finds anything on Soulseek you need a Soulseek account: see [Soulseek](/wondarr/sources/soulseek/).
