# Wondarr documentation site

The user documentation, built with [Starlight](https://starlight.astro.build/) (Astro). The repository's `docs/` folder is the build documentation; this site is for people running Wondarr.

## Run it locally

Node 22.12 or newer.

```bash
cd website
npm ci
npm run dev       # http://localhost:4321/wondarr/
npm run build     # builds to dist/ and fails on a broken internal link
npm run preview   # serves the built site
```

Pages are Markdown or MDX under `src/content/docs/`; the sidebar is in `astro.config.mjs`.
