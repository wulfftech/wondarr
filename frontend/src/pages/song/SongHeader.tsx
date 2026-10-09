import { Anchor, Badge, Button, Card, Flex, Group, Select, Stack, Switch, Text, Title } from '@mantine/core';
import { ArrowRightLeft, Disc3, Search, Trash2, UserSearch, Wand2 } from 'lucide-react';
import { Link } from 'react-router';
import { readEnum, type LibraryResource, type QualityProfileResource } from '../../api/profiles';
import type { SongDetailsResource, SongResource } from '../../api/songs';
import { PreviewButton } from '../../components/PreviewButton';
import { formatDuration } from '../../components/SongCells';
import { AlbumCover } from '../../components/song/AlbumOptionLabel';
import { initCaps } from '../../components/text';

/** The wire spelling of `AlbumContextKind`; the API serialises enums as camelCase strings. */
type AlbumContextKindName = 'album' | 'single' | 'ep' | 'compilation' | 'pseudoSingles';

/** The album line: `Album by Artist`, the track and disc, and the year. */
function albumLine(song: SongResource): string | null {
  const context = song.albumContext;

  if (context === null) {
    return null;
  }

  const parts: string[] = [];

  if (readEnum<AlbumContextKindName>(context.kind) === 'pseudoSingles') {
    parts.push(`Singles by ${context.albumArtist}`);
  } else {
    parts.push(`${context.albumTitle} by ${context.albumArtist}`);
  }

  if (context.trackNo !== null) {
    parts.push(
      context.totalTracks === null ? `track ${context.trackNo}` : `track ${context.trackNo} of ${context.totalTracks}`,
    );
  }

  if (context.discNo !== null && Number(context.discNo) > 1) {
    parts.push(`disc ${context.discNo}`);
  }

  if (context.date !== null && context.date !== '') {
    parts.push(context.date.slice(0, 4));
  }

  return parts.join(' · ');
}

/** What the header needs from the page; every change goes back through these. */
export interface SongHeaderProps {
  song: SongResource;
  details: SongDetailsResource | undefined;
  profiles: QualityProfileResource[];
  libraries: LibraryResource[];
  /** Whether the file meets the profile's cutoff; `null` when it cannot be told. */
  cutoffMet: boolean | null;
  searchOpen: boolean;
  onMonitored: (monitored: boolean) => void;
  onProfile: (profileId: number) => void;
  onSearch: () => void;
  searching: boolean;
  onToggleInteractive: () => void;
  onChangeAlbum: () => void;
  onConvert: () => void;
  onMove: () => void;
  onDelete: () => void;
}

/** The band at the top of the song page: the cover on the left, the song, its status and its actions. */
export function SongHeader({
  song,
  details,
  profiles,
  libraries,
  cutoffMet,
  searchOpen,
  onMonitored,
  onProfile,
  onSearch,
  searching,
  onToggleInteractive,
  onChangeAlbum,
  onConvert,
  onMove,
  onDelete,
}: SongHeaderProps) {
  const cover = song.albumContext?.coverUrl ?? details?.deezer?.albumCoverUrl ?? null;
  const album = albumLine(song);
  const library = libraries.find((candidate) => String(candidate.id) === String(song.libraryId));
  const tags = song.tags ?? [];

  return (
    <Card withBorder padding="md">
      <Flex direction={{ base: 'column', sm: 'row' }} gap="lg" align={{ base: 'stretch', sm: 'flex-start' }}>
        <AlbumCover url={cover} size={160} />

        <Stack gap="sm" style={{ minWidth: 0, flex: 1 }}>
          <Stack gap={2}>
            <Group gap="xs" align="center">
              <Title order={2} style={{ overflowWrap: 'anywhere' }}>
                {song.title}
              </Title>
              {song.versionFlags.map((flag) => (
                <Badge key={flag} variant="light" size="sm" color="grape">
                  {initCaps(flag)}
                </Badge>
              ))}
            </Group>

            <Text size="md">
              <Anchor component={Link} to={`/library?artistId=${song.primaryArtistId}`}>
                {song.artistCredit}
              </Anchor>
            </Text>

            {album !== null && (
              <Text size="sm" c="dimmed">
                {album}
              </Text>
            )}

            <Text size="xs" c="dimmed">
              {formatDuration(song.durationMs)}
            </Text>
          </Stack>

          <Group gap="sm" align="center">
            <Switch
              label="Monitored"
              checked={song.monitored}
              onChange={(event) => onMonitored(event.currentTarget.checked)}
            />

            <Select
              aria-label="Quality profile"
              size="xs"
              w={190}
              allowDeselect={false}
              data={profiles.map((profile) => ({ value: String(profile.id), label: profile.name }))}
              value={String(song.qualityProfileId)}
              onChange={(value) => {
                if (value !== null) {
                  onProfile(Number(value));
                }
              }}
            />

            {library !== undefined && (
              <Badge variant="outline" color="gray" size="lg">
                {library.name}
              </Badge>
            )}

            <Badge color={song.hasFile ? 'green' : 'red'} variant="light" size="lg">
              {song.hasFile ? 'Downloaded' : 'Missing'}
            </Badge>

            {cutoffMet !== null && (
              <Badge color={cutoffMet ? 'green' : 'orange'} variant="light" size="lg">
                {cutoffMet ? 'Cutoff met' : 'Cutoff not met'}
              </Badge>
            )}
          </Group>

          {tags.length > 0 && (
            <Group gap={6} aria-label="Tags">
              {tags.map((tag) => (
                <Badge key={tag} variant="light" color="teal" size="sm">
                  {tag}
                </Badge>
              ))}
            </Group>
          )}

          <Group gap="xs">
            <Button size="xs" leftSection={<Search size={14} />} loading={searching} onClick={onSearch}>
              Search
            </Button>
            <Button
              size="xs"
              variant={searchOpen ? 'filled' : 'light'}
              leftSection={<UserSearch size={14} />}
              aria-expanded={searchOpen}
              onClick={onToggleInteractive}
            >
              Interactive search
            </Button>
            <Button size="xs" variant="default" leftSection={<Disc3 size={14} />} onClick={onChangeAlbum}>
              Change album…
            </Button>
            <Button size="xs" variant="default" leftSection={<Wand2 size={14} />} onClick={onConvert}>
              Convert…
            </Button>
            {libraries.length > 1 && (
              <Button size="xs" variant="default" leftSection={<ArrowRightLeft size={14} />} onClick={onMove}>
                Move…
              </Button>
            )}
            <Button size="xs" variant="light" color="red" leftSection={<Trash2 size={14} />} onClick={onDelete}>
              Delete
            </Button>
            <PreviewButton deezerId={null} songId={Number(song.id)} />
          </Group>
        </Stack>
      </Flex>
    </Card>
  );
}
