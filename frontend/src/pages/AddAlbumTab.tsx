import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Group,
  Image,
  Progress,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { CircleAlert, Disc3 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import {
  useAddAlbum,
  useAlbumLookup,
  useReleases,
  useTracklist,
  type AlbumAddAcceptedResource,
  type AlbumReleaseResource,
  type AlbumSearchResultResource,
  type AlbumTrackResource,
} from '../api/albums';
import { useLibraries, useQualityProfiles, readEnum } from '../api/profiles';
import { partialNotice, useCommand, type CommandStatusName } from '../api/songs';
import { EmptyState, LoadingState } from '../components/DataState';
import { formatDuration } from '../components/SongCells';
import { initCaps } from '../components/text';

/**
 * The Add songs page's Album tab: search an album, pick the release and the tracks, and add the ones
 * the library does not hold yet — as songs pinned to that release (LIBRARY_OUTPUT §7.3).
 */

/** The `?album=source:id` value the Add songs page reads off the URL to open a tracklist directly. */
export interface AlbumPrefill {
  source: string;
  id: string;
}

/** The album the tab is working on: where it came from, and what to call it. */
interface ChosenAlbum {
  source: string;
  /** The release-group MBID, the release MBID, or the Deezer album id — see `isReleaseGroup`. */
  id: string;
  title: string | null;
  artist: string | null;
  /** True when `id` names a release group, so its releases still have to be listed. */
  isReleaseGroup: boolean;
}

/** The 64 px cover a search result shows, falling back to the same disc placeholder the search tab uses. */
function AlbumCover({ url }: { url: string | null }) {
  const [failed, setFailed] = useState(false);

  if (url === null || url === '' || failed) {
    return <Disc3 size={64} aria-hidden />;
  }

  return <Image src={url} alt="" w={64} h={64} radius="sm" fit="cover" onError={() => setFailed(true)} />;
}

/** How a result is labelled: the provider that found it. */
function sourceLabel(source: string): string {
  return source === 'musicbrainz' ? 'MusicBrainz' : 'Deezer only';
}

/** The key a track is added by: its recording MBID, else its Deezer track id. */
function trackKeyOf(track: AlbumTrackResource): string | null {
  if (track.mbRecordingId !== null && track.mbRecordingId !== '') {
    return track.mbRecordingId;
  }

  return track.deezerTrackId === null ? null : String(track.deezerTrackId);
}

/** The same key, for a track the list already knows carries one. */
function trackKey(track: AlbumTrackResource): string {
  return trackKeyOf(track) ?? `${String(track.disc)}:${String(track.position)}`;
}

/** The disc and position a track sits at, the disc named only when there is more than one. */
function trackPosition(track: AlbumTrackResource): string {
  return Number(track.disc) > 1 ? `${String(track.disc)}.${String(track.position)}` : String(track.position);
}

/** One release of the select: title, date, country, formats and track count in one line. */
function releaseLabel(release: AlbumReleaseResource): string {
  const parts = [
    release.title,
    release.date,
    release.country,
    release.formats,
    release.trackCount === null ? null : `${String(release.trackCount)} tracks`,
  ].filter((part): part is string => part !== null && part !== '');

  return parts.join(' · ');
}

/**
 * What a progress message such as "Added 12 of 50 songs" says, or `null` when it says nothing. The
 * same reader the paste tab uses, kept here so the two tabs share no module cycle.
 */
function parseProgress(message: string | null | undefined): { done: number; total: number } | null {
  const match = /(\d+)\s+of\s+(\d+)/.exec(message ?? '');

  return match === null ? null : { done: Number(match[1]), total: Number(match[2]) };
}

/** One line of the album search: what it is, where it came from, and the Choose that opens it. */
function ResultRow({ result, onChoose }: { result: AlbumSearchResultResource; onChoose: () => void }) {
  return (
    <Card withBorder padding="sm">
      <Group wrap="nowrap" align="center" gap="md">
        <AlbumCover url={result.coverUrl} />

        <Stack gap={4} flex={1}>
          <Text size="sm" fw={500}>
            {result.title}
          </Text>

          <Text size="xs" c="dimmed">
            {result.artist}
          </Text>

          <Group gap="md">
            {result.year !== null && (
              <Text size="xs" c="dimmed">
                {result.year}
              </Text>
            )}
            {result.type !== null && (
              <Badge size="xs" variant="light">
                {initCaps(result.type)}
              </Badge>
            )}
            {result.trackCount !== null && (
              <Text size="xs" c="dimmed">
                {String(result.trackCount)} tracks
              </Text>
            )}
            <Badge size="xs" variant="outline">
              {sourceLabel(result.source)}
            </Badge>
          </Group>
        </Stack>

        <Button size="compact-sm" onClick={onChoose}>
          Choose
        </Button>
      </Group>
    </Card>
  );
}

/** The Album tab: search, choose the release, tick the tracks, and add the ones that are missing. */
export function AddAlbumTab({ prefill }: { prefill: AlbumPrefill | null }) {
  const lookup = useAlbumLookup();
  const [term, setTerm] = useState('');
  const [chosen, setChosen] = useState<ChosenAlbum | null>(() =>
    prefill === null
      ? null
      : { source: prefill.source, id: prefill.id, title: null, artist: null, isReleaseGroup: false },
  );
  const [pickedReleaseId, setPickedReleaseId] = useState<string | null>(null);
  const [monitored, setMonitored] = useState(true);
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [profileId, setProfileId] = useState<string | null>(null);
  const [accepted, setAccepted] = useState<AlbumAddAcceptedResource | null>(null);

  const add = useAddAlbum();
  const libraries = useLibraries();
  const profiles = useQualityProfiles();
  const command = useCommand(accepted === null ? null : Number(accepted.commandId), { poll: true });

  const releases = useReleases(chosen !== null && chosen.isReleaseGroup ? chosen.id : null);

  const releaseList = releases.data ?? [];
  const defaultRelease = releaseList.find((release) => release.isDefault) ?? releaseList[0] ?? null;
  const releaseId =
    chosen === null ? null : chosen.isReleaseGroup ? (pickedReleaseId ?? defaultRelease?.id ?? null) : chosen.id;

  const tracklist = useTracklist(chosen === null ? null : chosen.source, releaseId);
  const tracks = tracklist.data ?? [];
  const selectable = tracks.filter((track) => !track.owned && trackKeyOf(track) !== null);
  const allKeys = selectable.map(trackKey);

  /**
   * The ticked tracks, carried with the tracklist they belong to: a new tracklist reads as "every
   * track the library does not hold", so a change of release needs no effect to reset the selection.
   */
  const [selection, setSelection] = useState<{ tracks: AlbumTrackResource[]; picked: Set<string> | null }>({
    tracks: [],
    picked: null,
  });
  const picked = selection.tracks === tracks ? selection.picked : null;

  const setPicked = (next: Set<string> | null) => setSelection({ tracks, picked: next });

  const isSelected = (key: string) => picked === null || picked.has(key);
  const selectedCount = picked === null ? selectable.length : picked.size;

  const toggle = (key: string, wanted: boolean) => {
    const next = new Set(picked ?? allKeys);

    if (wanted) {
      next.add(key);
    } else {
      next.delete(key);
    }

    setPicked(next);
  };

  const libraryList = libraries.data ?? [];
  const defaultLibrary = libraryList.find((library) => library.isDefault) ?? libraryList[0] ?? null;
  const selectedLibraryId = libraryId ?? (defaultLibrary === null ? null : String(defaultLibrary.id));
  const firstProfile = profiles.data?.[0];
  const selectedProfileId = profileId ?? (firstProfile === undefined ? null : String(firstProfile.id));

  const progress = parseProgress(command.data?.message);
  const status = readEnum<CommandStatusName>(command.data?.status);
  const running = command.data !== undefined && status !== 'completed' && status !== 'failed' && status !== 'aborted';

  const submit = (event: FormEvent) => {
    event.preventDefault();

    if (term.trim() !== '') {
      lookup.mutate(term);
    }
  };

  const choose = (result: AlbumSearchResultResource) => {
    setChosen({
      source: result.source,
      id: result.id,
      title: result.title,
      artist: result.artist,
      isReleaseGroup: result.source === 'musicbrainz',
    });
    setPickedReleaseId(null);
    setAccepted(null);
  };

  const onAdd = () => {
    if (chosen === null || releaseId === null) {
      return;
    }

    add.mutate(
      {
        source: chosen.source,
        id: releaseId,
        // Every track the library does not hold is the default, so the keys travel only when the
        // user narrowed the selection.
        ...(picked === null ? {} : { trackKeys: [...picked] }),
        libraryId: selectedLibraryId === null ? null : Number(selectedLibraryId),
        qualityProfileId: selectedProfileId === null ? null : Number(selectedProfileId),
        monitored,
      },
      { onSuccess: setAccepted },
    );
  };

  const results = lookup.data?.items ?? [];
  const notice = partialNotice(lookup.data?.partial ?? []);

  return (
    <Stack gap="md">
      <form onSubmit={submit}>
        <Group align="flex-end">
          <TextInput
            label="Album"
            placeholder="Artist - Album, or an album title"
            value={term}
            flex={1}
            onChange={(event) => setTerm(event.currentTarget.value)}
          />
          <Button type="submit" loading={lookup.isPending}>
            Search
          </Button>
        </Group>
      </form>

      {lookup.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />} title="That search could not be run">
          {lookup.error.message}
        </Alert>
      )}

      {releases.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {releases.error.message}
        </Alert>
      )}

      {tracklist.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {tracklist.error.message}
        </Alert>
      )}

      {add.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {add.error.message}
        </Alert>
      )}

      {lookup.isPending && <LoadingState label="Searching…" />}

      {notice !== null && chosen === null && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          {notice}
        </Alert>
      )}

      {lookup.isSuccess && chosen === null && results.length === 0 && (
        <EmptyState message="No albums matched — try 'Artist - Album'" />
      )}

      {chosen === null &&
        results.map((result) => (
          <ResultRow key={`${result.source}:${result.id}`} result={result} onChoose={() => choose(result)} />
        ))}

      {chosen !== null && (
        <Stack gap="md">
          <Group justify="space-between" wrap="nowrap">
            <Stack gap={0}>
              <Text fw={600}>{chosen.title ?? 'Album'}</Text>
              {chosen.artist !== null && (
                <Text size="sm" c="dimmed">
                  {chosen.artist}
                </Text>
              )}
            </Stack>

            <Button
              variant="default"
              size="compact-sm"
              onClick={() => {
                setChosen(null);
                setAccepted(null);
              }}
            >
              Choose another album
            </Button>
          </Group>

          {chosen.isReleaseGroup && (
            <Select
              label="Release"
              description="The release the tracks and tags come from"
              data={releaseList.map((release) => ({ value: release.id, label: releaseLabel(release) }))}
              value={releaseId}
              allowDeselect={false}
              w={420}
              onChange={(value) => setPickedReleaseId(value)}
            />
          )}

          {tracklist.isPending && <LoadingState label="Loading the tracklist…" />}

          {tracklist.isSuccess && (
            <Stack gap="sm">
              <Group gap="xs" justify="space-between" wrap="nowrap">
                <Group gap="xs">
                  <Button variant="subtle" size="compact-sm" onClick={() => setPicked(null)}>
                    Select all
                  </Button>
                  <Button variant="subtle" size="compact-sm" onClick={() => setPicked(new Set())}>
                    Select none
                  </Button>
                </Group>

                <Text size="sm" c="dimmed">
                  {selectedCount} of {selectable.length} tracks selected
                </Text>
              </Group>

              <Table verticalSpacing="xs" horizontalSpacing="sm" withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th w={36} aria-label="Add" />
                    <Table.Th w={60}>#</Table.Th>
                    <Table.Th>Title</Table.Th>
                    <Table.Th>Artist</Table.Th>
                    <Table.Th w={90}>Length</Table.Th>
                    <Table.Th w={130} aria-label="Library" />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {tracks.map((track) => {
                    const key = trackKeyOf(track);
                    const owned = track.owned;

                    return (
                      <Table.Tr key={`${String(track.disc)}-${String(track.position)}-${key ?? track.title}`}>
                        <Table.Td>
                          <Checkbox
                            aria-label={`Add ${track.title}`}
                            checked={owned || (key !== null && isSelected(key))}
                            disabled={owned || key === null}
                            onChange={(event) => {
                              if (key !== null) {
                                toggle(key, event.currentTarget.checked);
                              }
                            }}
                          />
                        </Table.Td>
                        <Table.Td>
                          <Text size="sm" c="dimmed">
                            {trackPosition(track)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Text size="sm">{track.title}</Text>
                        </Table.Td>
                        <Table.Td>
                          <Text size="sm" c="dimmed">
                            {track.artistCredit}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Text size="sm" c="dimmed">
                            {formatDuration(track.lengthMs)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          {owned && (
                            <Text component={Link} size="xs" to="/library">
                              In the library
                            </Text>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>

              <Group justify="space-between" wrap="nowrap" align="flex-end">
                <Group gap="md" align="flex-end">
                  {libraryList.length > 1 && (
                    <Select
                      label="Library"
                      placeholder="The default library"
                      data={libraryList.map((library) => ({ value: String(library.id), label: library.name }))}
                      value={selectedLibraryId}
                      allowDeselect={false}
                      w={220}
                      onChange={setLibraryId}
                    />
                  )}

                  <Select
                    label="Quality profile"
                    placeholder="The default profile"
                    data={(profiles.data ?? []).map((profile) => ({
                      value: String(profile.id),
                      label: profile.name,
                    }))}
                    value={selectedProfileId}
                    allowDeselect={false}
                    w={220}
                    onChange={setProfileId}
                  />

                  <Switch
                    label="Monitored"
                    checked={monitored}
                    onChange={(event) => setMonitored(event.currentTarget.checked)}
                  />
                </Group>

                <Button
                  leftSection={<Disc3 size={16} />}
                  disabled={selectedCount === 0}
                  loading={add.isPending}
                  onClick={onAdd}
                >
                  Add {selectedCount} {selectedCount === 1 ? 'song' : 'songs'}
                </Button>
              </Group>
            </Stack>
          )}

          {accepted !== null && (
            <Card withBorder padding="md">
              <Stack gap="sm">
                <Text fw={600}>{running ? 'Adding…' : 'Added'}</Text>

                {command.data?.message !== null && command.data?.message !== undefined && (
                  <Text size="sm">{command.data.message}</Text>
                )}

                {progress !== null && (
                  <Progress
                    value={progress.total === 0 ? 0 : Math.round((progress.done / progress.total) * 100)}
                    aria-label="Add progress"
                  />
                )}

                {command.error !== null && (
                  <Alert color="red" icon={<CircleAlert size={16} />}>
                    {command.error.message}
                  </Alert>
                )}
              </Stack>
            </Card>
          )}
        </Stack>
      )}
    </Stack>
  );
}
