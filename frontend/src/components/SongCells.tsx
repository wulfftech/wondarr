import { Image } from '@mantine/core';
import { Disc3 } from 'lucide-react';
import { useState } from 'react';

/** The small pieces the song, history and blocklist tables share. */

/**
 * The 40 px album cover. `coverUrl` is null while a song has no album art, and an `<img>` can still
 * fail on a URL that has gone stale, so both cases fall back to the same lucide placeholder.
 */
export function CoverThumb({ url }: { url: string | null }) {
  const [failed, setFailed] = useState(false);

  if (url === null || url === '' || failed) {
    return <Disc3 size={40} aria-hidden />;
  }

  return <Image src={url} alt="" w={40} h={40} radius="sm" fit="cover" onError={() => setFailed(true)} />;
}

/**
 * `m:ss`, or `h:mm:ss` from one hour. The API sends the duration as an int32 that a client may also
 * read as a string, so both spellings are accepted; nothing readable reads as an em dash.
 */
// Shared by the song tables rather than by a component, so the fast-refresh export rule does not apply.
// eslint-disable-next-line react-refresh/only-export-components
export const formatDuration = (duration: number | string | null | undefined): string => {
  const ms = typeof duration === 'string' ? Number(duration) : duration;

  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) {
    return '—';
  }

  const totalSeconds = Math.round(ms / 1000);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const hours = Math.floor(totalSeconds / 3600);
  const ss = String(seconds).padStart(2, '0');

  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${ss}` : `${minutes}:${ss}`;
};

/** A short local date, or an em dash when the API sent nothing usable. */
// eslint-disable-next-line react-refresh/only-export-components
export const formatDate = (iso: string | null | undefined): string => {
  if (iso === null || iso === undefined || iso === '') {
    return '—';
  }

  const parsed = new Date(iso);

  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleDateString();
};
