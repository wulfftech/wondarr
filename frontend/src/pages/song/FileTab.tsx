import { Badge, Button, Group, Stack, Text } from '@mantine/core';
import { Search, UserSearch } from 'lucide-react';
import type { SongResource } from '../../api/songs';
import { formatDuration } from '../../components/SongCells';
import {
  formatBytes,
  formatChannels,
  formatDateTime,
  formatDecibels,
  formatSampleRate,
} from '../../components/song/format';
import { Block, Fact, FactGrid } from './Facts';

type SongFileResource = NonNullable<SongResource['file']>;

/** Providers a downloaded file can come from, as they read on screen. */
const PROVIDERS: Record<string, string> = {
  soulseek: 'Soulseek',
  youtube: 'YouTube',
  torrent: 'Torrent',
  usenet: 'Usenet',
};

/** Where the file came from, in a sentence a person reads. */
function sourceText(file: SongFileResource): string {
  const { kind, provider, name, referenceLibraryName, relativePath } = file.source;
  const library =
    referenceLibraryName === null ? 'a reference library' : `the reference library “${referenceLibraryName}”`;
  const where = relativePath === null ? library : `${library}, ${relativePath}`;

  if (kind === 'reference') {
    return `Your own file in ${where}`;
  }

  if (kind === 'adopted') {
    return `Copied from ${where}`;
  }

  if (kind === 'download') {
    const named = provider === null ? file.sourceType : (PROVIDERS[provider.toLowerCase()] ?? provider);

    return name === null || name === '' ? named : `${named}: ${name}`;
  }

  return PROVIDERS[file.sourceType.toLowerCase()] ?? file.sourceType;
}

/** The file section: what is on disk and where it came from, or the empty state with the search buttons. */
export function FileTab({
  song,
  qualityNames,
  onSearch,
  onInteractiveSearch,
}: {
  song: SongResource;
  qualityNames: ReadonlyMap<string, string>;
  onSearch: () => void;
  onInteractiveSearch: () => void;
}) {
  const file = song.file ?? null;

  if (!song.hasFile || file === null) {
    return (
      <Block title="File">
        <Stack gap="sm" align="flex-start">
          <Text size="sm" c="dimmed">
            No file. This song has not been downloaded yet.
          </Text>
          <Group gap="xs">
            <Button size="xs" variant="light" leftSection={<Search size={14} />} onClick={onSearch}>
              Search for a file
            </Button>
            <Button size="xs" variant="default" leftSection={<UserSearch size={14} />} onClick={onInteractiveSearch}>
              Interactive search
            </Button>
          </Group>
        </Stack>
      </Block>
    );
  }

  const quality = qualityNames.get(String(file.qualityId)) ?? `#${file.qualityId}`;
  const bitrate = file.bitrateKbps === null ? null : `${file.bitrateKbps} kbps`;
  const onDisk = [file.codec.toUpperCase(), bitrate].filter((part) => part !== null).join(' · ');

  return (
    <Block title="File">
      <Fact label="Path">
        <Text component="span" size="sm" ff="monospace">
          {file.path}
        </Text>
      </Fact>

      <FactGrid>
        <Fact label="Quality (as graded)">{quality}</Fact>
        <Fact label="On disk">{onDisk}</Fact>
        <Fact label="Codec">{file.codec}</Fact>
        <Fact label="Container">{file.container}</Fact>
        <Fact label="Bitrate">{bitrate ?? '—'}</Fact>
        <Fact label="Sample rate">{formatSampleRate(file.sampleRate)}</Fact>
        <Fact label="Bit depth">{file.bitDepth === null ? '—' : `${file.bitDepth}-bit`}</Fact>
        <Fact label="Channels">{formatChannels(file.channels)}</Fact>
        <Fact label="Length">{formatDuration(file.durationMs)}</Fact>
        <Fact label="Size">{formatBytes(file.size)}</Fact>
        <Fact label="ReplayGain">
          {file.replayGainDb === null && file.replayGainPeak === null
            ? '—'
            : `${formatDecibels(file.replayGainDb)} · peak ${file.replayGainPeak ?? '—'}`}
        </Fact>
        <Fact label="Imported">{formatDateTime(file.importedAt)}</Fact>
      </FactGrid>

      <Fact label="AcoustID">
        <Group gap="xs">
          <Text component="span" size="sm" ff="monospace">
            {file.acoustId ?? '—'}
          </Text>
          {file.fingerprintVerified ? (
            <Badge color="green" variant="light" size="sm">
              Fingerprint verified
            </Badge>
          ) : (
            <Badge color="gray" variant="light" size="sm">
              Not fingerprint verified
            </Badge>
          )}
        </Group>
      </Fact>

      <Fact label="Source">{sourceText(file)}</Fact>
    </Block>
  );
}
