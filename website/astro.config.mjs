// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
  site: 'https://wulfftech.github.io',
  base: '/wondarr',
  integrations: [
    starlight({
      title: 'Wondarr',
      description: 'The *arr for single songs: Soulseek, YouTube Music, torrents and usenet, with verified imports.',
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/wulfftech/wondarr' }],
      editLink: { baseUrl: 'https://github.com/wulfftech/wondarr/edit/main/website/' },
      plugins: [starlightLinksValidator()],
      sidebar: [
        { label: 'Home', slug: 'index' },
        {
          label: 'Getting started',
          items: [
            { slug: 'getting-started/install' },
            { slug: 'getting-started/unraid' },
            { slug: 'getting-started/first-run' },
          ],
        },
        {
          label: 'Sources',
          items: [
            { slug: 'sources/soulseek' },
            { slug: 'sources/plex' },
            { slug: 'sources/qbittorrent' },
            { slug: 'sources/indexers' },
            { slug: 'sources/sabnzbd' },
            { slug: 'sources/youtube' },
          ],
        },
        {
          label: 'Guides',
          items: [{ slug: 'guides/end-to-end' }],
        },
      ],
    }),
  ],
});
