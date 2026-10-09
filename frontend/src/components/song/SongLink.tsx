import { Anchor } from '@mantine/core';
import type { ReactNode } from 'react';
import { Link } from 'react-router';

/** The path of a song's own page. */
// eslint-disable-next-line react-refresh/only-export-components
export const songPath = (songId: number | string): string => `/song/${songId}`;

/**
 * A song's title as a link to its page. A real anchor, so middle-click and "open in a new tab" work,
 * and a click on it does not toggle whatever row it sits in.
 */
export function SongLink({ songId, children }: { songId: number | string; children: ReactNode }) {
  return (
    <Anchor component={Link} to={songPath(songId)} size="sm" onClick={(event) => event.stopPropagation()}>
      {children}
    </Anchor>
  );
}
