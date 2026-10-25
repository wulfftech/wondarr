# Packaging

The canonical copies of Wondarr's entries in other projects' app catalogues. Each folder is what gets proposed upstream, so a change starts here and is then copied into a PR on that project. Why each channel was picked, and what it requires, is in [`docs/build/DISTRIBUTION.md`](../docs/build/DISTRIBUTION.md).

| Folder | Goes to | Path there |
|---|---|---|
| `truenas/wondarr/` | [truenas/apps](https://github.com/truenas/apps) | `ix-dev/community/wondarr/` |
| `cosmos/Wondarr/` | [azukaar/cosmos-servapps-official](https://github.com/azukaar/cosmos-servapps-official) | `servapps/Wondarr/` |
| `dockstarter/wondarr/` | [GhostWriters/DockSTARTer-Templates](https://github.com/GhostWriters/DockSTARTer-Templates) | `.apps/wondarr/` |

The Unraid template stays in [`unraid/`](../unraid/), because its `TemplateURL` and the docs point there.

## How each one was tested

| Folder | Test |
|---|---|
| `truenas/` | TrueNAS's own `.github/scripts/ci.py --app wondarr --train community --test-file basic-values.yaml`, on a Linux Docker host (not production). It renders the compose file, starts the app and waits for it to turn healthy. A restart on an existing `/config` and a `docker stop` were checked by hand: healthy again, and a clean shutdown in about 2 s. `ci.py` also vendors `templates/library/`, fills in `lib_version_hash`, `capabilities` and `run_as_context`, writes `item.yaml`, and bumps `version` on every run (reset it to the version you mean before copying back). truenas/apps gitignores the library, so it lives in neither place. |
| `cosmos/` | The store's own validator, `node .github/scripts/validate-servapps.js Wondarr`, in a throwaway Node container. It checks the schema, renders the template and checks the repository, image and architectures live. |
| `dockstarter/` | The compose fragments merged with the placeholders filled in, through `docker compose config`. The runtime settings (`cap_drop: ALL`, the six capabilities, `no-new-privileges`) are the ones the TrueNAS test ran with. |

## Upkeep

- **TrueNAS** pins `ix_values.yaml` to a version and digest. Their Renovate bot bumps it after each release, so nothing needs doing here. Copy their bump back if this folder is used for a later change.
- **Cosmos** and **DockSTARTer** use `:latest`, which follows the highest stable release.
- A new port, volume or setting the image needs is a change to every folder here, and to `unraid/wondarr.xml`.
