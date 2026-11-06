import { Alert, Button, Checkbox, Group, Modal, Stack, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import { useSongSearchCommand } from '../../api/queue';
import { useRemoveSongFile, type SongResource } from '../../api/songs';

/** The confirmation before a wrong file is taken away from its song. */
export function RemoveFileModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title="Remove file">
      {/* Keyed by the song, and unmounted with the dialog, so one song's refusal never reaches the next. */}
      <RemoveFileForm key={song?.id} song={song} onClose={onClose} />
    </Modal>
  );
}

function RemoveFileForm({ song, onClose }: { song: SongResource | null; onClose: () => void }) {
  const remove = useRemoveSongFile();
  const search = useSongSearchCommand();
  const [blocklist, setBlocklist] = useState(true);
  const [searchAgain, setSearchAgain] = useState(true);

  return (
    <Stack gap="md">
      <Text size="sm">
        Remove the file of “{song?.title ?? ''}”? It goes to the recycle bin, and the song is missing again, so
        Wondarr looks for it on its next search.
      </Text>
      {song?.file?.path !== undefined && (
        <Text size="xs" c="dimmed" style={{ wordBreak: 'break-all' }}>
          {song.file.path}
        </Text>
      )}

      <Checkbox
        label="Blocklist the download it came from"
        description="No search grabs that file for this song again."
        checked={blocklist}
        onChange={(event) => setBlocklist(event.currentTarget.checked)}
      />
      <Checkbox
        label="Search for the song now"
        checked={searchAgain}
        onChange={(event) => setSearchAgain(event.currentTarget.checked)}
      />

      {remove.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {remove.error.message}
        </Alert>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          color="red"
          loading={remove.isPending}
          onClick={() => {
            if (song !== null) {
              const id = Number(song.id);

              remove.mutate(
                { id, blocklist },
                {
                  onSuccess: () => {
                    if (searchAgain) {
                      search.mutate(id);
                    }

                    onClose();
                  },
                },
              );
            }
          }}
        >
          Remove file
        </Button>
      </Group>
    </Stack>
  );
}
