import { Alert, Button, Group, Loader, Modal, Radio, ScrollArea, Stack, Text, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useQueryClient } from '@tanstack/react-query';
import { CircleAlert, Search } from 'lucide-react';
import { useEffect, useState } from 'react';
import { readEnum } from '../../api/profiles';
import {
  invalidateLibrary,
  useAlbumOptions,
  useCommand,
  useSetAlbum,
  type CommandStatusName,
  type SongResource,
} from '../../api/songs';
import { AlbumGroupRow } from './AlbumGroupRow';
import { AlbumOptionLabel } from './AlbumOptionLabel';
import { editionText, groupAlbumOptions, groupMatches } from './albumGroups';

/** The picker gets a filter box once it has more albums (groups of editions) than this (a popular song can have 185). */
const ALBUM_FILTER_THRESHOLD = 10;

/** What the dialog tells the user about the file, before, while and after it moves. */
const REFILE_NOTE = "The file will be moved to the new album's folder.";
const MOVING_NOTE = "Moving the file to the new album's folder…";
const MOVED_NOTE = "The file was moved to the new album's folder.";

/** The source type of a file the user owns: Wondarr never moves or retags it. */
const REFERENCE_SOURCE = 'reference';

/**
 * The album picker: the releases a song could be filed under, and its artist's Singles album. Picking
 * one re-files a managed file (moves it to the new album's folder and retags it), which is a command of
 * its own, so the dialog follows it until it finishes and then refreshes the song.
 */
export function ChangeAlbumModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  const options = useAlbumOptions(opened && song !== null ? Number(song.id) : null);
  const setAlbum = useSetAlbum();
  const queryClient = useQueryClient();
  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCommand(commandId, { poll: true });
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [filter, setFilter] = useState('');
  // Groups the user opened or closed; a group not listed is open only when it holds the current album.
  const [toggled, setToggled] = useState<Record<string, boolean>>({});

  const status = readEnum<CommandStatusName>(command.data?.status);
  const finished =
    command.data !== undefined && (status === 'completed' || status === 'failed' || status === 'aborted');
  const failed = status === 'failed' || status === 'aborted';
  // A reference file is the user's own and is never moved; a song with no file has nothing to move.
  const managedFile = song?.file != null && song.file.sourceType !== REFERENCE_SOURCE;

  // The file is on disk at its new path now, so the song the user is looking at is stale.
  useEffect(() => {
    if (finished) {
      invalidateLibrary(queryClient);
    }
  }, [finished, queryClient]);

  const all = options.data ?? [];
  const current = all.find((option) => option.isCurrent)?.key ?? null;
  const groups = groupAlbumOptions(all);
  const needle = filter.trim().toLowerCase();
  const shown = needle === '' ? groups : groups.filter((group) => groupMatches(group, needle));

  const choose = (key: string | null) => {
    if (key === null || song === null || key === current) {
      return;
    }

    setAlbum.mutate(
      { id: Number(song.id), albumKey: key },
      {
        onSuccess: (changed) => {
          if (changed.refileCommandId == null) {
            onClose();
          } else {
            setCommandId(Number(changed.refileCommandId));
          }
        },
      },
    );
  };

  const close = () => {
    setCommandId(null);
    onClose();
  };

  return (
    <Modal opened={opened} onClose={close} title="Change album" size="min(900px, 100%)" fullScreen={isPhone}>
      {commandId !== null ? (
        <Stack gap="md">
          {!finished && (
            <Group gap="sm">
              <Loader size="sm" />
              <Text size="sm">{MOVING_NOTE}</Text>
            </Group>
          )}

          {finished && (
            <Alert color={failed ? 'red' : 'green'} icon={failed ? <CircleAlert size={16} /> : undefined}>
              {failed
                ? (command.data?.exception ?? command.data?.message ?? 'The file could not be moved.')
                : MOVED_NOTE}
            </Alert>
          )}

          {command.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {command.error.message}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button onClick={close}>{finished ? 'Close' : 'Close and let it finish'}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          {managedFile && (
            <Text size="sm" c="dimmed">
              {REFILE_NOTE}
            </Text>
          )}

          {options.isPending && (
            <Text size="sm" c="dimmed">
              Loading the album options…
            </Text>
          )}

          {options.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {options.error.message}
            </Alert>
          )}

          {setAlbum.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {setAlbum.error.message}
            </Alert>
          )}

          {groups.length > ALBUM_FILTER_THRESHOLD && (
            <TextInput
              aria-label="Filter albums"
              placeholder="Filter by title or album artist"
              leftSection={<Search size={14} />}
              value={filter}
              onChange={(event) => setFilter(event.currentTarget.value)}
            />
          )}

          <ScrollArea.Autosize mah={isPhone ? '70dvh' : '60vh'} type="auto" offsetScrollbars>
            <Radio.Group value={current} onChange={choose}>
              <Stack gap="md">
                {shown.map((group) => {
                  if (group.editions.length === 1) {
                    const option = group.editions[0];

                    return <Radio key={group.id} value={option.key} label={<AlbumOptionLabel option={option} />} />;
                  }

                  const open = toggled[group.id] ?? group.hasCurrent;

                  return (
                    <AlbumGroupRow
                      key={group.id}
                      view={group.view}
                      open={open}
                      onToggle={() => setToggled({ ...toggled, [group.id]: !open })}
                    >
                      {group.editions.map((option) => (
                        <Radio
                          key={option.key}
                          value={option.key}
                          label={
                            <Text component="span" size="sm">
                              {editionText(option)}
                            </Text>
                          }
                        />
                      ))}
                    </AlbumGroupRow>
                  );
                })}
                {needle !== '' && shown.length === 0 && (
                  <Text size="sm" c="dimmed">
                    No album matches “{filter}”.
                  </Text>
                )}
              </Stack>
            </Radio.Group>
          </ScrollArea.Autosize>
        </Stack>
      )}
    </Modal>
  );
}
