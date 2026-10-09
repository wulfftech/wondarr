---
title: Soulseek
description: The bundled slskd, your Soulseek account, sharing, and using your own slskd instead.
---

Soulseek is the first place Wondarr looks. The image bundles slskd (the Soulseek client) and runs it for you. You never configure slskd by hand: everything is under **Settings → Soulseek**.

## Use a dedicated account

Give Wondarr its own Soulseek account. Soulseek allows one login per account, so a desktop client signed in with the same account kicks Wondarr off, and slskd does not reconnect after being kicked. Wondarr reports this as "slskd was kicked: another client logged in using the same username. Use a dedicated account."

## Settings → Soulseek

The page opens with a status card (slskd's version and state, who it is logged in as, how many folders and files it shares, the search allowance) and a **Connection** card. Below them is the form.

**Account**

- **Username** and **Password**. The password is never shown again; the box reads "(set)" once one is stored, and **Clear password** removes it.
- **Listen port**: default `50300`, between 1024 and 65535. If you change it, change the port you publish and forward as well.

**Sharing**

- **Share my library**: on by default. Soulseek users often ban peers who share nothing, so with it off the page warns that downloads may fail or be refused, and Wondarr raises a health warning.
- **Shared folders**: by default your library root, `/data/music`. **Add folder** shares more. After imports, Wondarr asks slskd to rescan its shares, so new files are shared.

**Uploads**

- **Upload slots**: default `10`.
- **Upload speed limit (KiB/s)**: default `2000`; `0` means unlimited.

**Directories**

- **Downloads directory**: default `/data/downloads/slskd`. Wondarr moves completed files out of it.
- **Incomplete directory**: default `/data/downloads/slskd/incomplete`.

**Network**

- **Distributed network**: on by default.

Press **Save**. A change to the credentials, shared folders, slots, directories or the distributed network restarts slskd ("Saved — slskd restarts to apply it"). The listen port and speed limit are applied live.

### Settings set by the environment

Any of these can be fixed from the container's environment instead. The field is then greyed out with a lock, and the tooltip names the variable:

| Setting | Variable |
|---|---|
| Username | `APP__SOULSEEK__USERNAME` |
| Password | `APP__SOULSEEK__PASSWORD` |
| Listen port | `APP__SOULSEEK__LISTEN_PORT` |
| Share my library | `APP__SOULSEEK__SHARE_LIBRARY` |
| Shared folders | `APP__SOULSEEK__SHARED_FOLDERS__0`, `…__1`, one per folder |
| Upload slots | `APP__SOULSEEK__UPLOAD_SLOTS` |
| Upload speed limit | `APP__SOULSEEK__UPLOAD_SPEED_LIMIT_KIB` |
| Distributed network | `APP__SOULSEEK__DISTRIBUTED_NETWORK` |
| Downloads directory | `APP__SOULSEEK__DOWNLOADS_DIR` |
| Incomplete directory | `APP__SOULSEEK__INCOMPLETE_DIR` |

```yaml
environment:
  - APP__SOULSEEK__USERNAME=your-soulseek-user
  - APP__SOULSEEK__PASSWORD=your-password
```

## The search budget

Soulseek polices how fast an account searches. Wondarr holds itself well inside the network's limits, and the configuration refuses values above them:

- at most 30 searches in any 4 minutes (`soulseek.search.max_searches`, `window_seconds`),
- at most 2 searches in flight at once (`soulseek.search.max_outstanding`),
- at least 5 seconds between submissions (`soulseek.search.min_spacing_seconds`).

A search waits for peers to answer for about 8 seconds (`search_timeout_ms`) and is cancelled after 30 seconds in all. The status card shows how many searches were used in the last 4 minutes. With a large pasted list, songs are searched one after another, so a big list takes a while: that is by design.

## Health messages

Wondarr checks slskd and reports problems in the health badge at the top of the page:

- slskd's state, version and Soulseek login. A rejected login ("the Soulseek username or password was rejected") and a duplicate login are read from slskd's log and shown on the status card.
- The slskd download folder exists and is writable.
- Sharing: a warning when **Share my library** is off, or when none of the shared folders exists.
- Media tools (`ffprobe`, `ffmpeg`, `fpcalc`) answer. Without them downloads cannot be verified.

## Use your own slskd

If you already run slskd, use it instead of the bundled one. It must be version 0.26.0 or newer.

In **Settings → Soulseek → Connection**, choose **My own slskd** and fill in:

- **slskd URL**, for example `http://slskd:5030`.
- **API key**: an API key from your slskd (`web.authentication.api_keys`). The box reads "Stored — type to replace" once saved, and the key is never shown again.
- **Ask slskd to rescan its shares after each import**: off by default. Your slskd decides what it shares.

Press **Test**, then **Save connection**. Switching mode takes effect when Wondarr restarts.

The same in `config.yml`, or as environment variables:

```yaml
soulseek:
  mode: external
  downloads_dir: /data/downloads/slskd
  external:
    url: http://slskd:5030
    api_key: your-api-key
    rescan_shares: false
```

| Setting | Variable |
|---|---|
| `soulseek.mode` | `APP__SOULSEEK__MODE` (`bundled` or `external`) |
| `soulseek.external.url` | `APP__SOULSEEK__EXTERNAL__URL` |
| `soulseek.external.api_key` | `APP__SOULSEEK__EXTERNAL__API_KEY` (16 to 255 characters) |
| `soulseek.downloads_dir` | `APP__SOULSEEK__DOWNLOADS_DIR` |

In external mode the account, shares, slots, speed limit, listen port, distributed network and incomplete directory belong to your slskd. They are shown read-only, and a change is refused with "change it in slskd's settings". Only the **Downloads directory** stays editable, and it has a new meaning: it is where Wondarr sees your slskd's download folder. Mount that folder into the Wondarr container (usually at the same path), because Wondarr moves completed files out of it. The same search budget applies to your slskd.
