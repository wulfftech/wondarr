import { Alert, Anchor, Button, Group, Loader, Stack, Text, TextInput } from '@mantine/core';
import { CircleAlert, ExternalLink } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useAppConfig } from '../api/context';
import { useResolveMatch, type MatchQueueItemResource } from '../api/references';
import { useReadYouTubeVideo, type YouTubeVideoResource } from '../api/youtube';
import {
  forgetSeed,
  musicBrainzSeed,
  newSeedToken,
  openSeededEditor,
  rememberSeed,
  releaseDateParts,
  returnAddress,
  seedDefaults,
  type MusicBrainzReturn,
} from './musicBrainzSeed';

/**
 * "Not on MusicBrainz?" in the Match drawer (DECISIONS build session 14 #33): a short form, filled in from
 * the file, that opens MusicBrainz' release editor with the song already entered. The user finishes it
 * there under their own account; MusicBrainz then sends the browser back to the Match page, which
 * settles the file with the new release.
 */

/** How far the video's length may be from the file's before the form says so. */
const LENGTH_WARNING_MS = 10_000;

/** `m:ss`. */
function minutesAndSeconds(ms: number): string {
  const seconds = Math.round(ms / 1000);

  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/** One line about the video Wondarr read: channel, upload date, length. */
function videoLine(video: YouTubeVideoResource): string {
  return [
    video.channel,
    video.uploadDate === null ? null : `uploaded ${video.uploadDate}`,
    video.durationMs === null ? null : minutesAndSeconds(Number(video.durationMs)),
  ]
    .filter((part): part is string => part !== null && part !== '')
    .join(' · ');
}

/** The form, folded away behind one button until the user wants it. */
export function AddToMusicBrainz({ item }: { item: MatchQueueItemResource }) {
  const config = useAppConfig();
  const read = useReadYouTubeVideo();
  const defaults = seedDefaults(item);
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState(defaults.title);
  const [artist, setArtist] = useState(defaults.artist);
  const [link, setLink] = useState('');
  const [releaseDate, setReleaseDate] = useState('');
  const [video, setVideo] = useState<YouTubeVideoResource | null>(null);
  const [sent, setSent] = useState(false);

  const fileMs = item.file.durationMs === null ? null : Number(item.file.durationMs);
  // What was read belongs to the link it was read for, not to one typed in since.
  const details = video !== null && video.url === link.trim() ? video : null;
  const videoMs = details === null || details.durationMs === null ? null : Number(details.durationMs);
  const dateParts = releaseDateParts(releaseDate);
  const ready = title.trim() !== '' && artist.trim() !== '' && dateParts !== null;

  const readVideo = () =>
    read.mutate(link.trim(), {
      onSuccess: (details) => {
        setVideo(details);
        setLink(details.url);

        if (releaseDate.trim() === '' && details.uploadDate !== null) {
          setReleaseDate(details.uploadDate);
        }
      },
      onError: () => setVideo(null),
    });

  const openEditor = () => {
    const token = newSeedToken();
    const id = Number(item.id);

    rememberSeed(id, item.relativePath, token);
    openSeededEditor(
      musicBrainzSeed({
        title,
        artist,
        durationMs: fileMs,
        releaseDate,
        link,
        linkDetails: details,
        returnTo: returnAddress(config.urlBase, id, token),
      }),
    );
    setSent(true);
  };

  if (!open) {
    return (
      <Stack gap={4}>
        <Text fw={600} size="sm">
          Not on MusicBrainz?
        </Text>
        <Text size="xs" c="dimmed">
          Add it there and MusicBrainz sends you back here, where this file is settled with it. You need a MusicBrainz
          account.
        </Text>
        <Group>
          <Button size="xs" variant="default" onClick={() => setOpen(true)}>
            Add to MusicBrainz…
          </Button>
        </Group>
      </Stack>
    );
  }

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        Add to MusicBrainz
      </Text>
      <Text size="xs" c="dimmed">
        Opens MusicBrainz&apos; release editor in a new tab with this song filled in as a digital single. Link the
        artist there, or add them, check the rest, and submit.
      </Text>

      <TextInput label="Title" required value={title} onChange={(event) => setTitle(event.currentTarget.value)} />
      <TextInput label="Artist" required value={artist} onChange={(event) => setArtist(event.currentTarget.value)} />

      <Group gap="xs" align="flex-end" wrap="nowrap">
        <TextInput
          label="YouTube link"
          description="Optional: where the song was released."
          placeholder="https://www.youtube.com/watch?v=…"
          style={{ flex: 1 }}
          value={link}
          onChange={(event) => setLink(event.currentTarget.value)}
        />
        <Button variant="light" loading={read.isPending} disabled={link.trim() === ''} onClick={readVideo}>
          Read video
        </Button>
      </Group>

      {details !== null && (
        <Text size="xs" c="dimmed">
          {videoLine(details)}
        </Text>
      )}

      {videoMs !== null && fileMs !== null && Math.abs(videoMs - fileMs) > LENGTH_WARNING_MS && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          The video runs {minutesAndSeconds(videoMs)} and the file {minutesAndSeconds(fileMs)}. Check that it is the
          same song.
        </Alert>
      )}

      {read.error !== null && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          {read.error.message} The link still goes to MusicBrainz as you typed it.
        </Alert>
      )}

      <TextInput
        label="Release date"
        description="Optional. For a song released on YouTube, the day it was uploaded."
        placeholder="YYYY-MM-DD"
        value={releaseDate}
        error={dateParts === null ? 'Use YYYY, YYYY-MM or YYYY-MM-DD, or leave it empty.' : undefined}
        onChange={(event) => setReleaseDate(event.currentTarget.value)}
      />

      {sent && (
        <Alert color="blue">
          MusicBrainz opened in a new tab. When you submit the release there, MusicBrainz sends you back to Wondarr and
          this file is settled with it.
        </Alert>
      )}

      <Group gap="xs">
        <Button leftSection={<ExternalLink size={14} />} disabled={!ready} onClick={openEditor}>
          {sent ? 'Open MusicBrainz again' : 'Open MusicBrainz'}
        </Button>
        <Button variant="subtle" onClick={() => setOpen(false)}>
          Cancel
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * What the Match page shows when MusicBrainz sends the browser back after a release was submitted. A seed
 * this browser opened is settled at once; anything else (a link someone else made, a browser without
 * storage) is shown and settled only when the user says so.
 */
export function MusicBrainzReturnNotice({
  returned,
  path,
  onDone,
}: {
  returned: MusicBrainzReturn;
  /** The file's path when the page knows it, for a way back this browser did not start. */
  path: string | null;
  onDone: () => void;
}) {
  const resolve = useResolveMatch();
  const started = useRef(false);
  const file = returned.pending?.path ?? path ?? `reference file ${returned.fileId}`;
  const releaseUrl = `https://musicbrainz.org/release/${returned.releaseId}`;

  const settle = () =>
    resolve.mutate(
      { id: returned.fileId, mbReleaseId: returned.releaseId },
      // Forgetting the seed tells the tab that opened it (the storage event) to refresh its queue.
      { onSuccess: () => forgetSeed(returned.fileId, returned.token) },
    );

  const dismiss = () => {
    forgetSeed(returned.fileId, returned.token);
    onDone();
  };

  useEffect(() => {
    // Once, however often the effect runs (StrictMode runs it twice).
    if (returned.pending !== null && !started.current) {
      started.current = true;
      settle();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the way back is settled once, on arrival.
  }, []);

  // Only a seed this browser opened is "the release you added"; a link from anywhere else is not.
  const ours = returned.pending !== null;
  const release = (
    <Anchor href={releaseUrl} target="_blank" rel="noopener noreferrer">
      {ours ? 'the release you added' : 'this MusicBrainz release'}
    </Anchor>
  );

  if (resolve.isSuccess) {
    return (
      <Alert color="green" withCloseButton onClose={onDone} title="Settled">
        {file} is now the song on {release}.{resolve.data.message ? ` ${resolve.data.message}` : ''}
      </Alert>
    );
  }

  if (resolve.isPending) {
    return (
      <Alert color="blue" icon={<Loader size={16} />}>
        Settling {file} with {release}…
      </Alert>
    );
  }

  if (resolve.isError) {
    return (
      <Alert color="yellow" icon={<CircleAlert size={16} />} title={`${file} is not settled yet`}>
        <Stack gap="xs">
          <Text size="sm">{resolve.error.message}</Text>
          {ours && (
            <Text size="xs" c="dimmed">
              Until you dismiss this, Wondarr tries again each time you open Match.
            </Text>
          )}
          <Group gap="xs">
            <Button size="xs" onClick={settle}>
              Try again
            </Button>
            <Button size="xs" variant="subtle" onClick={dismiss}>
              Dismiss
            </Button>
          </Group>
        </Stack>
      </Alert>
    );
  }

  return (
    <Alert color="blue" title="Settle with a MusicBrainz release?">
      <Stack gap="xs">
        <Text size="sm">
          This link asks to settle {file} with {release}. It was not opened from this browser, so check the release
          first.
        </Text>
        <Group gap="xs">
          <Button size="xs" onClick={settle}>
            Settle
          </Button>
          <Button size="xs" variant="subtle" onClick={dismiss}>
            Dismiss
          </Button>
        </Group>
      </Stack>
    </Alert>
  );
}
