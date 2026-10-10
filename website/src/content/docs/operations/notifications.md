---
title: Notifications
description: Send a webhook, a Discord message or an Apprise notification when Wondarr grabs, imports or fails.
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

## Secrets

Secret fields show as `********`. Leave the mask in place when you save and the stored value is kept. Notifications never block Wondarr: a slow or failing target is given a limited time and then dropped, and no URL or credential is written to the log.
