import { Stack, Text } from '@mantine/core';
import { useSongLyrics } from '../../api/songs';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { lyricLines } from '../../components/song/format';
import { Block } from './Facts';

/** Where the lyrics came from, as the note under them reads. */
const SOURCES: Record<string, string> = {
  sidecar: 'the lyrics file next to the song',
  lrclib: 'LRCLIB',
};

/**
 * Lyrics. This component is mounted only while its tab is open, so the lookup (which can reach LRCLIB)
 * runs when the person asks for it and not on every visit to the page.
 */
export function LyricsTab({ songId }: { songId: number }) {
  const lyrics = useSongLyrics(songId, true);

  if (lyrics.isPending) {
    return <LoadingState label="Looking for lyrics…" />;
  }

  if (lyrics.error !== null) {
    return <ErrorState message={lyrics.error.message} />;
  }

  const synced = lyrics.data.synced;
  const plain = lyrics.data.plain;
  const lines =
    synced !== null && synced !== '' ? lyricLines(synced) : plain !== null && plain !== '' ? plain.split(/\r?\n/) : [];

  if (lines.length === 0) {
    return <EmptyState message="No lyrics found." />;
  }

  const source = lyrics.data.source === null ? null : (SOURCES[lyrics.data.source] ?? lyrics.data.source);

  return (
    <Block title="Lyrics">
      <Stack gap={0} aria-label="Lyrics">
        {lines.map((line, index) => (
          // The lines are static text and have no identity of their own, so the position is the key.
          <Text key={index} size="sm" mih="1.25em">
            {line}
          </Text>
        ))}
      </Stack>
      {source !== null && (
        <Text size="xs" c="dimmed">
          Source: {source}
        </Text>
      )}
    </Block>
  );
}
