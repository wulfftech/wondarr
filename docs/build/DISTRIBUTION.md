# Distribution channels

Where Wondarr is listed, what is waiting, and the rules for the rest. Researched on 2026-10-24 from each channel's own rules; re-read the linked source before submitting anything, because the rules change. The catalogue entries themselves are in [`packaging/`](../../packaging/).

## Status (2026-11-10)

| Channel | State | Upkeep per release |
|---|---|---|
| GHCR `ghcr.io/wulfftech/wondarr` | Primary. `:latest`, `:0.1`, `:0.1.x`, `:develop`; amd64 and arm64 | None |
| Docker Hub [`wulfftech/wondarr`](https://hub.docker.com/r/wulfftech/wondarr) | Live since 2026-10-25. `mirror-dockerhub.yml` copies every promoted tag | None |
| GitHub description, homepage, topics | Set 2026-10-25 | None |
| TrueNAS apps, community train | Live since 2026-11-09 ([truenas/apps#6245](https://github.com/truenas/apps/pull/6245)); [catalogue page](https://apps.truenas.com/catalog/wondarr_community/) | None: their Renovate bumps the digest pin |
| Cosmos | PR [azukaar/cosmos-servapps-official#335](https://github.com/azukaar/cosmos-servapps-official/pull/335), validator passed, waiting on review | None (`:latest`) |
| DockSTARTer | PR [GhostWriters/DockSTARTer-Templates#188](https://github.com/GhostWriters/DockSTARTer-Templates/pull/188), waiting on review. Uses `/storage` with `WONDARR_MEDIA_ROOT=/storage` (0.1.16) | None (`:latest`) |
| Unraid Community Applications | Template ready in `unraid/`. Submitting at ca.unraid.net needs the maintainer's Unraid login | None (`:latest`) |
| selfh.st apps directory | Not submitted. A web form, done by hand | None |
| r/selfhosted, awesome-selfhosted, awesome-arr | Date-gated; see [Dates](#dates-to-come-back-to). Written by a person | None |
| Umbrel, CasaOS / ZimaOS | Held: each release needs a pinned-version PR | High |

When a channel goes live, add it to the **Where to get it** table on the docs site's Install page and to the README's "Get it running".

## Hand-written submissions

These channels refuse AI-written contributions. The README discloses Wondarr's AI use, which covers the code; the submission itself must be written and opened by a person, so this doc lists the facts an entry needs and no drafted text.

| Channel | Rule |
|---|---|
| awesome-selfhosted | "Machine/LLM-generated contributions are not allowed and will result in a ban." ([CONTRIBUTING](https://github.com/awesome-selfhosted/awesome-selfhosted-data/blob/master/CONTRIBUTING.md)) |
| awesome-arr | "This PR was written by a human", and AI use disclosed in the project. ([PR template](https://github.com/Ravencentric/awesome-arr/blob/main/.github/pull_request_template.md)) |
| Homepage (widgets) | No AI-generated PRs. ([CONTRIBUTING](https://github.com/gethomepage/homepage/blob/main/CONTRIBUTING.md)) |
| Saltbox Sandbox | No roles generated wholesale by AI. ([README](https://github.com/saltyorg/Sandbox/blob/master/README.md)) |

The other channels state no such rule. Their PR bodies say the packaging was prepared with AI help and tested by the maintainer.

## Live channels: what to know

- **Docker Hub.** The `promote` job in `release.yml` writes the same digest to Docker Hub with `docker buildx imagetools create`, so only images that passed the smoke matrix are mirrored. It skips when the `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` secrets are unset. The Hub page comes from `docker/DOCKERHUB.md` (25,000-byte limit). Docker-Sponsored Open Source (no pull limits, a badge) is open to Wondarr: public, OSI licence, updated in the last six months, no commercial path ([DSOS](https://docs.docker.com/docker-hub/repos/manage/trusted-content/dsos-program/)).
- **TrueNAS.** Default host ports 30536 (web UI) and 30537 (Soulseek) came from their `port_validation.py`. `APP__SERVER__PORT` and `APP__SOULSEEK__LISTEN_PORT` make the container listen on the same numbers, as their slskd app does. The app starts as root and drops to `PUID`/`PGID` (568 by default) with `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETGID`, `SETUID` and `KILL`: without `DAC_OVERRIDE` the init can't create `/config/logs`, and without `KILL` s6 can't stop the app gracefully. `packaging/truenas/wondarr/` is at 0.1.14; copy their latest `ix_values.yaml` back before proposing a change.

## Waiting channels

### Unraid Community Applications

- **How:** sign in at https://ca.unraid.net/submit, give it the repository, run Scan and Validate, add listing details, submit. Approved apps appear after the next catalogue build; about a day, anecdotally.
- **Requires:** a public repository with an OSI licence, a template XML whose `TemplateURL` is its own raw URL, `ca_profile.xml` at the root with a non-empty `<Profile>`, and support links. No age or star minimum. ([submit page](https://ca.unraid.net/submit), [CA docs](https://docs.unraid.net/community-applications/))
- **Wondarr has:** `unraid/wondarr.xml` (Category `MediaApp:Music Downloaders:`), `ca_profile.xml`, `LICENSE`.
- **Unverified:** the starter repo keeps templates under `templates/`, ours are in `unraid/`; Scan shows whether it finds them. If not, move the template and keep the old path working.

### Cosmos

A small platform whose store has Lidarr, Prowlarr, Radarr, Sonarr and Navidrome. The last new app took 23 days to merge. Behind Cosmos's reverse proxy Wondarr can't show its local-only first-login form, so the template sets `APP__SERVER__AUTH=external` and turns on Cosmos's authentication for the route.

### DockSTARTer

A menu-driven installer for the \*arr stack, with Lidarr, Prowlarr and about a dozen other \*arrs. The template mounts `DOCKER_VOLUME_STORAGE` at `/storage` and sets `WONDARR_MEDIA_ROOT=/storage`, so download clients and Plex see the same paths and torrent imports hard-link. (Two bind mounts of one folder don't hard-link: `ln: Cross-device link`, checked 2026-10-25.)

### selfh.st

The form at https://selfh.st/submit/. No criteria are stated. Its weekly newsletter covers new self-hosted software.

### Umbrel and CasaOS / ZimaOS

Both list Lidarr, so downloaders are welcome; both pin each image, so every release means another PR and another wait (CasaOS additions took 24 to 34 days). Hold them until releases slow to every week or two.

- **Umbrel:** a PR adding `wondarr/` with `umbrel-app.yml` and a `docker-compose.yml` routed through `app_proxy`. Images multi-arch and pinned as `tag@sha256:<index digest>`, a store-unique `port` (1077 is free), storage under the app's data directory, `gallery: []` and no `icon`. Lint with `npm run lint:apps` and test on umbrelOS. ([umbrel-apps](https://github.com/getumbrel/umbrel-apps))
- **CasaOS / ZimaOS:** a PR adding `Apps/Wondarr/docker-compose.yml` with an `x-casaos` block: reverse-domain `id`, `main`, `index`, `port_map`, `icon` (SVG preferred), `thumbnail`, `screenshot_link`, `title`, `tagline` and `description` (at least `en_US`), `category: Media`, `architectures`. Run `./scripts/build_dist.sh` first. ([compose spec](https://github.com/IceWhaleTech/CasaOS-AppStore/blob/main/docs/specs/compose-and-x-casaos.md))

### r/selfhosted

Projects younger than three months go in the New Project Megathread only (from a third-party summary; Reddit blocked the rules page). A standalone post opens around 2026-12-28. The maintainer posts it.

### awesome-selfhosted

Eligible from 2027-02-10 ("first released more than 4 months ago", counted from `v0.1.0`). Pinchflat and LidaTube are listed with the "depends on a proprietary service" marker, so the YouTube source doesn't disqualify Wondarr. The entry is `software/wondarr.yml`: `name`, `website_url` (the docs site), `source_code_url`, a `description` without "self-hosted", "free" or "open-source", `licenses: [GPL-3.0]`, `platforms: [Docker]`, `tags: [Media Management]`, `depends_3rdparty: true`. Reviews take 12 to 19 days.

### awesome-arr

Eligible from 2027-10-10 (a year after the first release), with about 100 stars as a soft rule and activity in the last 12 months. Music projects such as SoulSync sit under "Beyond \*arr". The entry is `- [Name](link) - Description`, alphabetical. Too-new projects are closed within two days.

## Not now

| Channel | Why not | Look again |
|---|---|---|
| Proxmox VE community scripts | Needs 600+ stars, six months and native release tarballs; "We do NOT use Docker". ([DevScripts](https://github.com/community-scripts/DevScripts)) | If Wondarr ships native builds |
| Runtipi | "No new applications will be accepted." ([README](https://github.com/runtipi/runtipi-appstore/blob/master/README.md)) | If they reopen |
| Saltbox Sandbox | Wants broad adoption; no AI-generated roles | After the awesome lists accept it |
| Homepage widget | A real integration, and no AI-generated PRs | When someone writes it by hand |
| ElfHosted | Hosted, not self-hosted; requests go through their Discord | Once there's demand |
| linuxserver.io | No public request process; the image already follows their conventions | — |
| Not researched | YunoHost, StartOS, AUR, Nix, ArtifactHub/Helm, Portainer/Coolify/Easypanel templates, AlternativeTo | When native artifacts exist, or a user asks |

Umbrel, CasaOS and Runtipi also accept third-party store repositories by URL. A Wondarr-owned store skips review but needs its own public repository and upkeep, and reaches only people who already know Wondarr.

## Dates to come back to

| Date | What opens up |
|---|---|
| 2026-12-28 | r/selfhosted standalone post (three months since the repository was created) |
| 2027-02-10 | awesome-selfhosted |
| 2027-04-10 | Proxmox VE scripts' six-month bar (stars and native builds still block it) |
| 2027-10-10 | awesome-arr, if stars are near 100 |
