import { Badge, Center, Group, Image, Stack, Text } from '@mantine/core';
import { Disc3 } from 'lucide-react';
import { useState } from 'react';
import { initCaps } from '../text';

/**
 * One row of the album picker and of the song page's "Appears on" list: cover, title, album artist,
 * badges and the song's place on the release. It takes the fields the two resources share, so both
 * `AlbumOptionResource` and `SongReleaseResource` fit.
 */
export interface AlbumOptionView {
  title: string;
  albumArtist: string;
  primaryType: string | null;
  secondaryTypes: string[];
  status: string | null;
  date: string | null;
  trackNo: number | string | null;
  totalTracks: number | string | null;
  coverUrl: string | null;
  isVariousArtists?: boolean;
  originalDate?: string | null;
  discNo?: number | string | null;
}

/** The song's track position on an album option, as the dialog words it. */
function trackText(option: AlbumOptionView): string | null {
  const parts: string[] = [];

  if (option.trackNo != null && option.totalTracks != null) {
    parts.push(`Track ${option.trackNo} of ${option.totalTracks}`);
  } else if (option.totalTracks != null) {
    parts.push(`${option.totalTracks} tracks`);
  }

  if (option.discNo != null && Number(option.discNo) > 1) {
    parts.push(`disc ${option.discNo}`);
  }

  return parts.length > 0 ? parts.join(' · ') : null;
}

/** The release year, with the first-release year when it differs: `2004 · first released 1999`. */
function dateText(option: AlbumOptionView): string | null {
  if (option.date == null) {
    return null;
  }

  const released = option.date.slice(0, 4);
  const original = option.originalDate?.slice(0, 4);

  return original != null && original !== '' && original !== released
    ? `${released} · first released ${original}`
    : released;
}

/** A square album cover, lazy-loaded, with a neutral placeholder when there is none or it fails. */
export function AlbumCover({ url, size = 64 }: { url: string | null | undefined; size?: number }) {
  const [failed, setFailed] = useState(false);

  if (url == null || url === '' || failed) {
    return (
      <Center w={size} h={size} bg="var(--mantine-color-default-hover)" style={{ borderRadius: 4, flexShrink: 0 }}>
        <Disc3 size={Math.round(size / 2)} aria-hidden />
      </Center>
    );
  }

  return (
    <Image
      src={url}
      alt=""
      w={size}
      h={size}
      radius="sm"
      fit="cover"
      loading="lazy"
      style={{ flexShrink: 0 }}
      onError={() => setFailed(true)}
    />
  );
}

export function AlbumOptionLabel({ option }: { option: AlbumOptionView }) {
  const isCompilation = option.isVariousArtists === true || option.secondaryTypes.includes('Compilation');
  const track = trackText(option);
  const date = dateText(option);
  const details = [track, date].filter((part) => part !== null).join(' · ');

  return (
    <Group component="span" gap="sm" wrap="nowrap" align="flex-start">
      <AlbumCover url={option.coverUrl} />
      <Stack component="span" gap={2}>
        <Text component="span" size="sm" fw={700}>
          {option.title}
        </Text>
        <Text component="span" size="xs" c="dimmed">
          by {option.albumArtist}
        </Text>
        <Group component="span" gap={6}>
          {isCompilation && (
            <Badge size="xs" variant="light" color="grape">
              Compilation
            </Badge>
          )}
          {option.primaryType !== null && (
            <Badge size="xs" variant="light">
              {initCaps(option.primaryType)}
            </Badge>
          )}
          {option.secondaryTypes
            .filter((type) => type !== 'Compilation')
            .map((type) => (
              <Badge key={type} size="xs" variant="light">
                {initCaps(type)}
              </Badge>
            ))}
          {option.status !== null && option.status !== 'Official' && (
            <Badge size="xs" variant="light" color="yellow">
              {option.status}
            </Badge>
          )}
        </Group>
        {details !== '' && (
          <Text component="span" size="xs" c="dimmed">
            {details}
          </Text>
        )}
      </Stack>
    </Group>
  );
}
