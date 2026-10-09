# Migration fixtures

`phase0-compilarr.db` is a real database written by the Phase 0 image (`ghcr.io/wulfftech/compilarr:sha-6103320`, the commit that closed Phase 0): the image ran for two minutes with a manual `CheckHealth`, was stopped cleanly, and the slskd secrets in the `setting` row were replaced with placeholder text. It is the starting point of the upgrade tests (`Phase0UpgradeTests`, `Phase0UpgradeApiTests`) and must never be regenerated from a newer image, since its whole value is that it predates every later migration.
