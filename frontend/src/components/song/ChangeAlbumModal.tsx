import { Alert, Modal, Radio, ScrollArea, Stack, Text, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { CircleAlert, Search } from 'lucide-react';
import { useState } from 'react';
import { useAlbumOptions, useSetAlbum, type SongResource } from '../../api/songs';
import { AlbumGroupRow } from './AlbumGroupRow';
import { AlbumOptionLabel } from './AlbumOptionLabel';
import { editionText, groupAlbumOptions, groupMatches } from './albumGroups';

/** The picker gets a filter box once it has more albums (groups of editions) than this (a popular song can have 185). */
const ALBUM_FILTER_THRESHOLD = 10;

/** The album picker: the releases a song could be filed under, and its artist's Singles album. */
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
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [filter, setFilter] = useState('');
  // Groups the user opened or closed; a group not listed is open only when it holds the current album.
  const [toggled, setToggled] = useState<Record<string, boolean>>({});

  const all = options.data ?? [];
  const current = all.find((option) => option.isCurrent)?.key ?? null;
  const groups = groupAlbumOptions(all);
  const needle = filter.trim().toLowerCase();
  const shown = needle === '' ? groups : groups.filter((group) => groupMatches(group, needle));

  const choose = (key: string | null) => {
    if (key === null || song === null || key === current) {
      return;
    }

    setAlbum.mutate({ id: Number(song.id), albumKey: key }, { onSuccess: onClose });
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Change album" size="min(900px, 100%)" fullScreen={isPhone}>
      <Stack gap="md">
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
    </Modal>
  );
}
