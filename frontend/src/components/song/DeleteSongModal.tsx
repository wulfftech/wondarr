import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useDeleteSong, type SongResource } from '../../api/songs';

/** The confirmation before a song leaves the library. */
export function DeleteSongModal({
  song,
  opened,
  onClose,
  onDeleted,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
  /** Called once the delete went through, before the dialog closes (the song page leaves itself here). */
  onDeleted?: () => void;
}) {
  const remove = useDeleteSong();

  return (
    <Modal opened={opened} onClose={onClose} title="Delete song">
      <Stack gap="md">
        <Text size="sm">Delete “{song?.title ?? ''}” from the library? Any downloaded file is left where it is.</Text>

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
                remove.mutate(Number(song.id), {
                  onSuccess: () => {
                    onDeleted?.();
                    onClose();
                  },
                });
              }
            }}
          >
            Delete
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
