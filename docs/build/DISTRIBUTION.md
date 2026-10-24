# Distribution channels

Where Wondarr can be listed so people who never browse GHCR still find it. Researched on 2026-10-24 from each channel's own rules. These rules change, so re-read the linked source before submitting anything.

Today Wondarr ships one way: `ghcr.io/wulfftech/wondarr` (`:latest`, `:0.1`, `:0.1.x`, `:develop`; `linux/amd64` and `linux/arm64`). There is an Unraid template in `unraid/` and a `ca_profile.xml` at the root, but Wondarr isn't listed in Community Applications yet.

## Where Wondarr stands against the usual gates

| Gate | Wondarr on 2026-10-24 |
|---|---|
| Repository created | 2026-09-28 |
| First tagged release | `v0.1.0`, 2026-10-10 (an alpha tag `v0.0.1-alpha.1` exists from 2026-09-28) |
| Release cadence | 15 releases in 12 days (`v0.1.0` to `v0.1.14`) |
| GitHub stars | 1 |
| Licence | GPL-3.0 (OSI) |
| Image | GHCR only, multi-arch, smoke-tested on both architectures before tags move |
| Icon | `frontend/public/icon-512.png`, a 512×512 transparent PNG. No SVG. The docs site favicon (`website/public/favicon.svg`) is an older purple "W" that doesn't match it. |
| Screenshots | Five, 1440×860, in `docs/screenshots/` |
| Docs | https://wulfftech.github.io/wondarr/ |
| AI use disclosed | Yes, in the README |
| GitHub description, topics, homepage | All empty |
| Native release artifacts | None (Docker only) |

Two of those matter more than the rest. **Age** gates the curated lists. **Release cadence** decides how much upkeep each store costs, because the stores that pin an exact version or digest need a pull request for every release.

## The shortlist

Ranked by reach for an \*arr music app, multiplied by whether Wondarr can get in now, divided by the effort and upkeep.

| # | Channel | Fit | Can submit now? | Effort | Upkeep per release |
|---|---|---|---|---|---|
| 0 | GitHub repo metadata | Topic pages, GitHub search | Yes | Minutes | None |
| 1 | Unraid Community Applications | Very high: the \*arr crowd's home turf | Yes | Small | None (the template tracks `:latest`) |
| 2 | Docker Hub mirror | High: Synology's and QNAP's container apps search Docker Hub | Yes | Small | None (CI pushes it) |
| 3 | TrueNAS apps, community train | High: slskd, Lidarr and a dozen small new \*arrs are already there | Yes | Medium | Low (their Renovate bumps the pin) |
| 4 | Cosmos Cloud marketplace | Low–medium: small platform, has the \*arrs | Yes | Small | Low |
| 5 | DockSTARTer | Medium: an \*arr-centric installer | Yes | Small–medium | Low |
| 6 | selfh.st apps directory | Medium: the weekly newsletter readers | Yes | Minutes | None |
| 7 | Umbrel App Store | Medium: has Lidarr, Sonarr, Radarr, Prowlarr | Yes | Medium–large | High (a digest-pinned PR per release) |
| 8 | CasaOS / ZimaOS App Store | Medium | Yes, slow review | Medium | High (a pinned-version PR per release) |
| 9 | r/selfhosted | High reach | Megathread only until about 2026-12-28 | Minutes | None |
| 10 | awesome-selfhosted | High reach | From 2027-02-10 | Minutes | None |
| 11 | awesome-arr | Exactly the audience | From 2027-10-10, and wants about 100 stars | Minutes | None |

Not now: Proxmox VE community scripts, Runtipi, Saltbox Sandbox, a Homepage widget, ElfHosted, linuxserver.io. See [Not now](#not-now-and-when-to-look-again).

**Recommended order:** 0, 1, 2 and 3 now. 4, 5 and 6 when there's an hour spare. Hold 7 and 8 until releases slow to every week or two, because each release means another PR. 9, 10 and 11 are date-gated, so they go on the backlog.

## Hand-written submissions

Some of these projects ban AI-written contributions outright. Wondarr's code is fine for them, because the README discloses the AI use. The *submission* is the problem: it has to be written and opened by a person.

| Channel | Rule |
|---|---|
| awesome-selfhosted | "Machine/LLM-generated contributions are not allowed and will result in a ban." The PR template has a "done by a human" box. ([CONTRIBUTING](https://github.com/awesome-selfhosted/awesome-selfhosted-data/blob/master/CONTRIBUTING.md)) |
| awesome-arr | "This PR was written by a human." Also, any AI use must be disclosed in the project itself. ([PR template](https://github.com/Ravencentric/awesome-arr/blob/main/.github/pull_request_template.md)) |
| Homepage (widgets) | Doesn't accept AI-generated PRs. ([CONTRIBUTING](https://github.com/gethomepage/homepage/blob/main/CONTRIBUTING.md)) |
| Saltbox Sandbox | Roles generated wholesale by AI are barred. ([README](https://github.com/saltyorg/Sandbox/blob/master/README.md)) |

For those, this doc lists the facts an entry needs and stops there. No drafted entry text.

The other channels state no such rule. Their submissions can be prepared in this repo. Saying in the PR body that the packaging was prepared with AI help and tested by the maintainer is the honest default.

## Channel details

### 0. GitHub repo metadata

GitHub's description, topics and website fields are all empty. Topic pages like `selfhosted`, `arr`, `servarr`, `lidarr`, `soulseek` and `music-downloader` are how people browse for these apps on GitHub. Several directories also pull the description from there.

- **Missing:** a one-line description, topics, and the docs site as the homepage.
- **Effort:** `gh repo edit wulfftech/wondarr --description … --homepage … --add-topic …`. Minutes.

### 1. Unraid Community Applications

- **Audience and fit:** Unraid users install almost everything from CA's Apps tab, and every mainstream \*arr is in it. It's the best single fit.
- **How:** sign in with an Unraid account at https://ca.unraid.net/submit, give it the GitHub repository, and run Scan and Validate. Then add listing details and submit. Approved apps appear after the next catalogue build. ([submit page](https://ca.unraid.net/submit))
- **Requires:**
  - a public GitHub repository with an OSI licence;
  - a template XML per app, with a `<Repository>` and a `TemplateURL` that points at that exact file's raw URL;
  - `ca_profile.xml` at the repository root, with a non-empty `<Profile>`;
  - support links. ([submit page](https://ca.unraid.net/submit), [starter repo](https://github.com/unraid/unraid-community-apps-starter))

  No minimum age or stars was found. A forum support thread isn't required by the portal. Unraid's docs do expect maintainers to keep templates working and answer support questions. ([CA docs](https://docs.unraid.net/community-applications/), [CA policies](https://forums.unraid.net/topic/87144-ca-application-policies/))
- **Wondarr has:** `unraid/wondarr.xml` (with `TemplateURL`, `Support`, `Project`, `Icon`, and Category `MediaApp:Music Downloaders:`), plus `ca_profile.xml` and `LICENSE`.
- **Missing or unverified:**
  - The starter repo keeps templates under `templates/`. Ours live in `unraid/`. The portal's Scan step shows whether it finds them. If it doesn't, move the template and keep the old path working.
  - There's no published list of Category values. Validate checks ours.
  - Someone has to sign in with an Unraid account. That's the maintainer's own login.
- **Effort:** small. **Upkeep:** none per release, because the template pulls `:latest`.
- **Review time:** anecdotally about a day ([example](https://github.com/AngeloSha/uchiyomi/pull/50)). No published timeline.

### 2. Docker Hub mirror

- **Audience and fit:** Docker Hub is the registry that Synology Container Manager and QNAP Container Station search by default, and the one most "docker run" tutorials assume. A GHCR-only image doesn't show up in those searches.
- **Account:**
  - A free personal account gets unlimited public repositories.
  - Creating an organisation requires a paid plan. ([orgs](https://docs.docker.com/admin/organization/orgs/), [pricing](https://www.docker.com/pricing/))
  - So the namespace is a personal Docker ID called `wulfftech`. It looks unclaimed, because the public API returns nothing for it; confirm at sign-up.
  - Organisation access tokens need Team or Business, so CI uses a personal access token with read/write scope. ([access tokens](https://docs.docker.com/enterprise/security/access-tokens/))
- **Docker-Sponsored Open Source (DSOS):**
  - Eligibility: public images, an OSI licence, an update within the last six months, and no commercial path.
  - It gives a badge, removes pull limits for users and includes a Team subscription, renewed yearly.
  - No age or star minimum is stated. ([DSOS](https://docs.docker.com/docker-hub/repos/manage/trusted-content/dsos-program/), [application](https://www.docker.com/community/open-source/application/))
  - Worth applying for once the mirror exists.
- **Pull limits:** the docs give 100 pulls per 6 hours for anonymous users and 200 for signed-in personal accounts, but the pricing page words it differently. GHCR stays the primary registry. ([usage docs](https://docs.docker.com/docker-hub/usage/pulls/))
- **Workflow change:**
  - `release.yml` already moves tags with `docker buildx imagetools create` in the `promote` job. That command can write the same digest to `docker.io/wulfftech/wondarr` as well, once the job has logged in to Docker Hub. Only images that passed the smoke matrix get mirrored.
  - `peter-evans/dockerhub-description` keeps the Hub page in sync with a README. That README is limited to 25,000 bytes, and the short description to 100. ([dockerhub-description](https://github.com/peter-evans/dockerhub-description))
  - The step should skip cleanly when the secrets aren't set, so forks and a missing token never fail a release.
- **Missing:** the Docker ID, a token stored as the `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` Actions secrets, the workflow step, a Hub README, and doc lines naming both registries.
- **Effort:** small. **Upkeep:** none per release.

### 3. TrueNAS apps (community train)

- **Audience and fit:** TrueNAS SCALE / Community Edition's built-in Apps catalogue. The community train already carries slskd (added 2026-01-29), Lidarr, Prowlarr, Sonarr, Radarr and a long tail of small, new \*arrs (Toparr, Swiparr, Spottarr, Cantinarr). Upstream maintainers aren't required, and anyone can PR. ([catalogue](https://github.com/truenas/apps/tree/master/ix-dev/community), [CONTRIBUTIONS](https://github.com/truenas/apps/blob/master/CONTRIBUTIONS.md))
- **How:** a PR to `truenas/apps` adding `ix-dev/community/wondarr/`. Maintainers review it and upload the icon and screenshots to their CDN.
- **Requires:**
  - `app.yaml`, `ix_values.yaml`, `questions.yaml`, `README.md`, `templates/docker-compose.yaml` and `templates/test_values/basic-values.yaml`. `item.yaml` is generated.
  - The vendored library goes in via `devbox run copy-lib`.
  - Tests pass with `ci.py`, which deploys the app with Compose and waits up to 10 minutes for it to turn healthy.
  - Default ports come from `port_validation.py`, which hands out the next free port in 30000–40000. The container still listens on 1077.
  - Health checks are always defined, GHCR is preferred over Docker Hub, and image keys end in `image`. ([CONTRIBUTIONS](https://github.com/truenas/apps/blob/master/CONTRIBUTIONS.md))
  - Images are pinned as `tag@sha256:…`, and Renovate bumps them. ([lidarr ix_values](https://github.com/truenas/apps/blob/master/ix-dev/community/lidarr/ix_values.yaml))
  - TrueNAS "reserves the right to reject, remove, or unpublish any application at its sole discretion".
- **Running as root:** TrueNAS prefers non-root apps, but images that start as root and drop to `PUID`/`PGID` are accepted. Deluge, Calibre, Cleanuparr and LazyLibrarian run as root with `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETGID` and `SETUID` (115 apps declare `DAC_OVERRIDE`). The library sets `PUID`/`PGID` from the form's user and group.
  - Wondarr needs that set plus `KILL`, as HandBrake has. Without `DAC_OVERRIDE`, the init can't create `/config/logs` in the folder TrueNAS's permissions helper hands to user 568.
  - Without `KILL`, s6 (running as root) can't signal the app (running as 568), so a stop isn't graceful.
- **Review time:** recent new-app PRs merged in 0–6 days (Koel, Paperclip, Blackbriar, SpeedKetchup, October 2026).
- **Status:** `packaging/truenas/wondarr/` passes their `ci.py`, a restart and a clean stop. The default ports 30536 (web UI) and 30537 (Soulseek) came from `port_validation.py` on 2026-10-24. Wondarr's `APP__SERVER__PORT` and `APP__SOULSEEK__LISTEN_PORT` make the container use the same port numbers as the host, as their slskd app does.
- **Effort:** medium. **Upkeep:** low, because their Renovate bumps the digest pin.

### 4. Cosmos Cloud marketplace

- **Audience and fit:** Cosmos is a small self-hosting platform. Its official marketplace repo has 65 stars and already lists Lidarr, Prowlarr, Radarr, Sonarr and Navidrome. ([repo](https://github.com/azukaar/cosmos-servapps-official))
- **How:** a PR adding `servapps/Wondarr/`, with `description.json`, `cosmos-compose.json` or `docker-compose.yml`, `icon.png` and `screenshots/`. CI renders the template and checks the schema, images and architectures (`npm run validate`). Merges to `master` publish the store. ([README](https://github.com/azukaar/cosmos-servapps-official/blob/master/README.md), [template guide](https://github.com/azukaar/cosmos-servapps-official/blob/master/CREATING_A_MARKET_TEMPLATE.md))
- **Requires:** no stated age, star or licence rule.
- **Review time:** the last new app took 23 days (AspireUI, September 2026). Fixes merge within days.
- **Sign-in:** behind Cosmos's reverse proxy, Wondarr never shows the form that creates the first login, because that form is local-only. The template therefore sets `APP__SERVER__AUTH=external` and turns on Cosmos's own authentication for the route.
- **Status:** `packaging/cosmos/Wondarr/` passes the store's validator.
- **Effort:** small. **Upkeep:** low.

### 5. DockSTARTer

- **Audience and fit:** a menu-driven installer built around the \*arr stack. The main repo has 2.5k stars. App templates live in [DockSTARTer-Templates](https://github.com/GhostWriters/DockSTARTer-Templates), which already has Lidarr, Prowlarr, Huntarr and about a dozen other \*arrs.
- **How:** a PR adding `.apps/wondarr/`, about 15 small files: compose fragments per architecture, ports, storage, labels, `.env` defaults and a README. The templates repo's guide walks through adding an app, and GitHub Actions checks the PR. ([CONTRIBUTING](https://github.com/GhostWriters/DockSTARTer-Templates/blob/main/.github/CONTRIBUTING.md))
- **Storage:** DockSTARTer mounts shared storage at `/storage`. Wondarr's defaults live under `/data`, so its template mounts the storage folder at both paths.
- **Status:** `packaging/dockstarter/wondarr/` merges cleanly with `docker compose config`.
- **Effort:** small–medium. **Upkeep:** low (tags come from `.env`).

### 6. selfh.st apps directory

- **Audience and fit:** selfh.st runs a self-hosted apps directory and a weekly newsletter that covers new software. Its readers are the people most likely to try a new \*arr.
- **How:** the form at https://selfh.st/submit/. No criteria are stated.
- **Effort:** minutes, done by hand, because it's a web form.

### 7. Umbrel App Store

- **Audience and fit:** umbrelOS home servers. The official store has Lidarr, Sonarr, Radarr, Prowlarr, qBittorrent, Transmission, MeTube and Pinchflat, so downloaders are welcome. ([umbrel-apps](https://github.com/getumbrel/umbrel-apps))
- **How:** a PR adding `wondarr/` with `umbrel-app.yml`, a `docker-compose.yml` that routes the UI through Umbrel's `app_proxy`, and `exports.sh` only if needed.
- **Requires:**
  - Every image must be multi-arch and pinned as `tag@sha256:<index digest>`.
  - The app's `port` must be unique across the store. 1077 is free today.
  - Storage lives under the app's data directory.
  - Leave `gallery: []` and no `icon`. Umbrel makes the gallery from screenshots and the logo attached to the PR.
  - Lint with `npm run lint:apps`, then test on umbrelOS. ([packaging guide](https://github.com/getumbrel/umbrel-apps/blob/master/.agents/skills/umbrel-package-app/SKILL.md))
- **Missing:** everything above, plus an umbrelOS test box (a VM works). Not verified: review time, and how `/data` should map onto Umbrel's shared downloads folder.
- **Effort:** medium–large. **Upkeep:** high at today's cadence: a digest-bump PR per release, each waiting on review.

### 8. CasaOS / ZimaOS App Store

- **Audience and fit:** CasaOS and ZimaOS share the official store. It's a NAS-appliance crowd, and Lidarr is listed (pinned to `linuxserver/lidarr:3.1.0`). ([store](https://github.com/IceWhaleTech/CasaOS-AppStore))
- **How:** a PR adding `Apps/Wondarr/docker-compose.yml` with an `x-casaos` block. It needs:
  - a reverse-domain `id`, `main`, `index` and `port_map`;
  - `icon`, `thumbnail` and `screenshot_link`, with an SVG icon preferred;
  - `title`, `tagline` and `description` (at least `en_US`), `category: Media`, and `architectures`.

  Run `./scripts/build_dist.sh` before opening the PR. ([compose spec](https://github.com/IceWhaleTech/CasaOS-AppStore/blob/main/docs/specs/compose-and-x-casaos.md), [assets](https://github.com/IceWhaleTech/CasaOS-AppStore/blob/main/docs/specs/assets-and-i18n.md))
- **Review time:** merged additions took 24–34 days, and several PRs have been open since July 2026.
- **Missing:** the compose file, an SVG icon, a thumbnail, and a pinned tag per release.
- **Effort:** medium. **Upkeep:** high, like Umbrel.

### 9. r/selfhosted

Not a package repository, but it's where self-hosters hear about new apps first.

- **The rule:** projects younger than three months belong in the New Project Megathread only. Promoted apps must be production-ready and documented. This comes from a third-party summary, because Reddit blocked a direct read of the rules page. ([summary](https://willitstay.com/r/selfhosted))
- **When:** a megathread post works now. A standalone post becomes possible around 2026-12-28.
- **Who:** the maintainer posts it, by hand.

### 10. awesome-selfhosted

- **Audience and fit:** the largest curated self-hosting list. Lidarr, Sonarr and Radarr are on it. Pinchflat and LidaTube are listed with the ⚠ "depends on a proprietary service" marker, so the YouTube Music source doesn't disqualify Wondarr. ([media management](https://awesome-selfhosted.net/tags/media-management.html))
- **Requires:**
  - "first released more than 4 months ago", counted from a release, which makes Wondarr eligible from 2027-02-10;
  - actively maintained, with a FOSS licence and working install instructions. ([CONTRIBUTING](https://github.com/awesome-selfhosted/awesome-selfhosted-data/blob/master/CONTRIBUTING.md))
- **Entry facts:** a `software/wondarr.yml` file with:
  - `name`, `website_url` (the docs site) and `source_code_url`;
  - a short `description` without "self-hosted", "free" or "open-source";
  - `licenses: [GPL-3.0]`, `platforms: [Docker]`, `tags: [Media Management]` and `depends_3rdparty: true`.
- **Review time:** 12–19 days in recent examples ([#3134](https://github.com/awesome-selfhosted/awesome-selfhosted-data/pull/3134), [#3146](https://github.com/awesome-selfhosted/awesome-selfhosted-data/pull/3146), [#3158](https://github.com/awesome-selfhosted/awesome-selfhosted-data/pull/3158)).
- **Who:** written and opened by a person. See [Hand-written submissions](#hand-written-submissions).

### 11. awesome-arr

- **Audience and fit:** exactly the audience. Music projects like SoulSync sit under "Beyond \*arr".
- **Requires:**
  - at least one year since the first public release, which means 2027-10-10;
  - about 100 stars, a soft rule;
  - activity within about 12 months, and AI use disclosed in the project (the README already does this);
  - a PR written by a human. ([PR template](https://github.com/Ravencentric/awesome-arr/blob/main/.github/pull_request_template.md))

  Too-new projects are closed within 0–2 days, and accepted ones merge in 1–5 ([#140](https://github.com/Ravencentric/awesome-arr/pull/140), [#143](https://github.com/Ravencentric/awesome-arr/pull/143), [#146](https://github.com/Ravencentric/awesome-arr/pull/146)).
- **Entry format:** `- [Name](link) - Description`, in alphabetical order.

## Not now, and when to look again

| Channel | Why not | Look again |
|---|---|---|
| Proxmox VE community scripts | New scripts go through [DevScripts](https://github.com/community-scripts/DevScripts) and need 600+ stars, six months of age and official release tarballs. "We do NOT use Docker": the \*arrs and slskd install natively from upstream release archives. ([PR template](https://github.com/community-scripts/DevScripts/blob/main/.github/pull_request_template.md), [AGENTS.md](https://github.com/community-scripts/DevScripts/blob/main/AGENTS.md)) | Only if Wondarr ships native linux-x64/arm64 release archives and passes 600 stars. |
| Runtipi | "No new applications will be accepted"; PRs are limited to bug fixes. ([README](https://github.com/runtipi/runtipi-appstore/blob/master/README.md)) | If they reopen. A self-hosted Runtipi store repo is the alternative. |
| Saltbox Sandbox | Wants an established project with broad adoption, and rejects AI-generated roles. ([README](https://github.com/saltyorg/Sandbox/blob/master/README.md)) | After the awesome-lists accept it. |
| Homepage widget | A real integration rather than a listing, and Homepage doesn't take AI-generated PRs. | When someone wants to write it by hand. |
| ElfHosted | Hosted apps, not self-hosted. Requests go through their Discord. ([get help](https://docs.elfhosted.com/get-help/)) | Once there's demand. |
| linuxserver.io | No public process for requesting a new container was found, and Wondarr's image already follows their conventions. | — |
| Not researched | YunoHost and StartOS (native packaging), AUR and Nix (native packaging), ArtifactHub/Helm, Portainer/Coolify/Easypanel templates, AlternativeTo. | When native artifacts exist, or if a user asks. |

Umbrel, CasaOS and Runtipi all let users add third-party store repositories by URL. A Wondarr-owned store repo would skip review entirely. It would also need a separate public repository and its own upkeep, and it reaches only people who already know about Wondarr.

## Dates to come back to

| Date | What opens up |
|---|---|
| 2026-12-28 | r/selfhosted standalone post (three months since the repository was created; confirm whether they count from the first release instead) |
| 2027-02-10 | awesome-selfhosted (four months after `v0.1.0`) |
| 2027-04-10 | Proxmox VE scripts' six-month bar, though stars and native packaging still block it |
| 2027-10-10 | awesome-arr (one year after `v0.1.0`), if the star count is near 100 |
