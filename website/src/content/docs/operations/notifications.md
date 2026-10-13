---
title: Notifications
description: Send a webhook, a Discord message, an Apprise notification or an email when Wondarr grabs, imports or fails.
---

Notifications are under **Settings → Notifications**. Press **Add notification**, choose a type, then fill in the form. Every notification has a **Name**, an **Enabled** switch and the **Events** it listens to. **Test** sends a test message so you can check the setup before relying on it.

## Events

| Label in the form | When it is sent |
|---|---|
| On grab | A candidate was handed to a download source. |
| On import | A downloaded file became a library file. |
| On upgrade | A better file replaced the one a song already had. |
| On download failure | A grab or an import failed. |
| On health issue | A health check started failing (a new warning or error under **System**). The first check after start-up sets the baseline and does not notify. |
| On update available | A newer Wondarr release exists: "Wondarr 0.2.0 is available (you have 0.1.0)", with the release's address. Sent once per new version (a restart does not repeat it) and never by a development build. See [Upgrading](/wondarr/getting-started/install/#upgrading). |

## Types

### Webhook

Sends a JSON body to a URL.

- **URL**: an absolute `http` or `https` address.
- **Method**: **POST** (default) or **PUT**.
- **Username** and **Password**: for HTTP basic authentication.
- **Headers** (Advanced): extra headers as name and value pairs. Their values are shown in full, so put credentials in Username and Password, which are masked.

The body uses Lidarr's `eventType` values, so tools that read Lidarr's webhook keep working: `Grab`, `Download` (for both a first import and an upgrade, with `isUpgrade` telling them apart), `DownloadFailure`, `Health`, `Update` and `Test`. It also carries `instanceName` (always `Wondarr`), `applicationUrl` and a `song` object. An `Update` body carries an `update` object with `currentVersion`, `latestVersion` and `releaseUrl` instead of a song.

### Discord

Posts an embed to a channel.

- **Webhook URL**: the channel's webhook URL. It carries the channel's token, so it is never shown again.
- **Username**: the name to post as, if not Discord's default.
- **Avatar**: an avatar URL.
- **Author**: the author name on the embed; "Wondarr" if empty.

Long titles and messages are shortened to Discord's limits so a message is never refused.

### Apprise

Sends through an [Apprise API](https://github.com/caronc/apprise-api) server, which can reach most chat and push services.

- **Apprise Server URL**: the server, including `http(s)://` and the port.
- **Configuration Key**: the persistent-storage key to notify, for example `wondarr`. Leave it empty when using stateless URLs.
- **Stateless URLs**: one or more service URLs separated by commas. They carry service tokens, so they are never shown again. Leave empty when using a configuration key.
- **Notification Type**: how Apprise tags the message: `info` (default), `success`, `warning` or `failure`.
- **Tags**: notify only the services carrying these tags, separated by commas.
- **Username** and **Password**: for HTTP basic authentication.

### Email

Sends email over SMTP. Each email is HTML with a plain-text alternative.

- **SMTP host** and **Port**: the mail server. The port is 587 if you leave it empty.
- **Security**: **startTls** (default; port 587), **sslOnConnect** (encrypted from the start; port 465) or **none** (unencrypted; only for a server on your own network).
- **Username** and **Password**: the login. Leave both empty when the server needs none. The password is never shown again, and it is sent only to the host, port and security it was saved for: change any of those and you enter the password again.
- **From**: the sender address.
- **To**: one or more recipient addresses, separated by commas. Every address is checked when you save.
- **Wondarr address** (optional): for example `https://wondarr.example.com`. When it is set, each song in an email links to its page in Wondarr.
- **Delivery**: **each** (default) sends one email per song; **digest** is described below.
- **Digest window (minutes)**: from 5 to 1440, 30 by default. Used only when Delivery is **digest**.

**Test** sends a short test email.

**What an email holds.** For each song: the artist, title, album, source, quality and file size; for a failure, the reason. The subject reads "Wondarr: imported Artist – Title" (or "upgraded", or "failed"). Grab, health and update messages are short: the same title and text the other types send.

**Digest.** In digest mode the imports, upgrades and failures are held back and sent as one email when the window ends. A window starts at the first held event and lasts the number of minutes you set; it ends with one email such as "Wondarr: 12 imported, 1 failed (14:00–14:30)", whose body groups the songs into imported, upgraded and failed, in time order. A window with nothing in it sends nothing. Only **import**, **upgrade** and **failure** are batched; **grab**, **health** and **update** are always sent at once. Held events are stored in Wondarr's database, so a restart does not lose them. If the mail server cannot be reached the events are kept and tried again after a wait (2, 4, 8 minutes, and up to 30), and the log shows an error once it has failed three times in a row. A notification keeps at most 500 held events; past that the oldest are dropped, with a warning in the log. Switching a notification back to **each** sends what is held within half a minute, deleting a notification deletes what it was holding, and a notification you disable keeps what it holds and sends it when you enable it again. The subject shows the window's times, with dates when it crosses midnight or lasts over 12 hours.

**Covers.** When the song's album folder has a `cover.jpg` of at most 500 KB, the cover is embedded in the email, so it shows without loading anything from the internet. A digest embeds at most 25 covers, and songs from the same album share one. Any other song gets its cover as a normal image link instead (your mail client may block it until you allow images), or no image if Wondarr has no cover address for it.

**Gmail.** Turn on 2-step verification, create an [app password](https://myaccount.google.com/apppasswords), then use host `smtp.gmail.com`, port `587`, security **startTls** (or port `465` with **sslOnConnect**), your Gmail address as Username, the app password as Password, and your Gmail address as From.

**Fastmail.** Create an app password under **Settings → Privacy & Security → Integrations** with SMTP access, then use host `smtp.fastmail.com`, port `465`, security **sslOnConnect** (or port `587` with **startTls**), your Fastmail address as Username, the app password as Password, and your Fastmail address as From.

## Secrets

Secret fields show as `********`. Leave the mask in place when you save and the stored value is kept. Notifications never block Wondarr: a slow or failing target is given a limited time and then dropped, and no URL or credential is written to the log.
