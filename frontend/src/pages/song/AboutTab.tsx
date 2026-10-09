import { Badge, Button, Card, Group, Stack, Text, TextInput } from '@mantine/core';
import { Search } from 'lucide-react';
import { useState } from 'react';
import type { SongDetailsResource } from '../../api/songs';
import { AlbumOptionLabel } from '../../components/song/AlbumOptionLabel';
import { formatCount, formatDecibels } from '../../components/song/format';
import { Block, Fact, FactGrid, SourceLink } from './Facts';

type Release = SongDetailsResource['releases'][number];

/** Releases shown before "Show all": the current album plus this many others. */
const COLLAPSED_COUNT = 10;

const isCompilation = (release: Release): boolean => release.secondaryTypes.includes('Compilation');

/** Originals before compilations, then oldest first; a release with no date goes last. */
function compareReleases(a: Release, b: Release): number {
  if (isCompilation(a) !== isCompilation(b)) {
    return isCompilation(a) ? 1 : -1;
  }

  if (a.date === b.date) {
    return 0;
  }

  if (a.date === null) {
    return 1;
  }

  return b.date === null ? -1 : a.date.localeCompare(b.date);
}

function ReleaseRow({ release }: { release: Release }) {
  return (
    <Group gap="sm" wrap="nowrap" align="flex-start">
      <AlbumOptionLabel option={release} />
      {release.isCurrent && (
        <Badge size="sm" variant="filled">
          Current album
        </Badge>
      )}
    </Group>
  );
}

/**
 * Every release the song appears on. A popular song can be on hundreds, so only the current album and
 * the first few originals show until "Show all"; once expanded, a filter box narrows the list.
 */
function AppearsOn({ releases }: { releases: Release[] }) {
  const [expanded, setExpanded] = useState(false);
  const [filter, setFilter] = useState('');

  const current = releases.filter((release) => release.isCurrent);
  const others = releases.filter((release) => !release.isCurrent).sort(compareReleases);
  const needle = filter.trim().toLowerCase();

  const collapsed = [...current, ...others.slice(0, COLLAPSED_COUNT)];
  const all = [...current, ...others];
  const shown = !expanded
    ? collapsed
    : needle === ''
      ? all
      : all.filter(
          (release) =>
            release.title.toLowerCase().includes(needle) || release.albumArtist.toLowerCase().includes(needle),
        );

  return (
    <Block title="Appears on">
      {expanded && (
        <TextInput
          aria-label="Filter releases"
          placeholder="Filter by title or album artist"
          leftSection={<Search size={14} />}
          value={filter}
          onChange={(event) => setFilter(event.currentTarget.value)}
        />
      )}

      <Stack gap="md">
        {shown.map((release) => (
          <ReleaseRow key={release.key} release={release} />
        ))}

        {expanded && shown.length === 0 && (
          <Text size="sm" c="dimmed">
            No release matches “{filter}”.
          </Text>
        )}
      </Stack>

      {all.length > collapsed.length && (
        <Group>
          <Button
            size="xs"
            variant="subtle"
            onClick={() => {
              setExpanded(!expanded);
              setFilter('');
            }}
          >
            {expanded ? 'Show fewer' : `Show all ${all.length}`}
          </Button>
        </Group>
      )}
    </Block>
  );
}

/** About: what MusicBrainz and Deezer know about the song, and the releases it appears on. */
export function AboutTab({ details }: { details: SongDetailsResource }) {
  const { musicBrainz, deezer, releases } = details;

  if (musicBrainz === null && deezer === null && releases.length === 0) {
    return (
      <Card withBorder padding="md">
        <Text size="sm" c="dimmed">
          No outside information is available for this song right now.
        </Text>
      </Card>
    );
  }

  return (
    <Stack gap="md">
      {musicBrainz !== null && (
        <Block title="MusicBrainz">
          <FactGrid>
            <Fact label="First release date">{musicBrainz.firstReleaseDate ?? '—'}</Fact>
            <Fact label="Artist credit">{musicBrainz.artistCredit}</Fact>
            {musicBrainz.disambiguation !== null && musicBrainz.disambiguation !== '' && (
              <Fact label="Disambiguation">{musicBrainz.disambiguation}</Fact>
            )}
            <Fact label="ISRCs">
              {musicBrainz.isrcs.length === 0 ? (
                '—'
              ) : (
                <Group gap={4}>
                  {musicBrainz.isrcs.map((isrc) => (
                    <Badge key={isrc} variant="light" size="sm" color="gray" ff="monospace">
                      {isrc}
                    </Badge>
                  ))}
                </Group>
              )}
            </Fact>
          </FactGrid>
          <Group>
            <SourceLink url={musicBrainz.url}>View on MusicBrainz</SourceLink>
          </Group>
        </Block>
      )}

      {deezer !== null && (
        <Block title="Deezer">
          <FactGrid>
            <Fact label="BPM">
              {deezer.bpm === null || Number(deezer.bpm) === 0 ? '—' : Math.round(Number(deezer.bpm))}
            </Fact>
            <Fact label="Gain">{formatDecibels(deezer.gain)}</Fact>
            <Fact label="Popularity (Deezer rank)">{formatCount(deezer.rank)}</Fact>
            <Fact label="Explicit">{deezer.explicitLyrics ? 'Yes' : 'No'}</Fact>
            <Fact label="Release date">{deezer.releaseDate ?? '—'}</Fact>
          </FactGrid>
          <Group>
            <SourceLink url={deezer.url}>View on Deezer</SourceLink>
          </Group>
        </Block>
      )}

      {releases.length > 0 && <AppearsOn releases={releases} />}
    </Stack>
  );
}
