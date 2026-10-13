import { Alert, Button, Group, Modal, Select, Stack, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import { useLibraries } from '../../api/profiles';
import { useCommand, useMoveSongs, useRefreshSongsWhenFinished, type SongResource } from '../../api/songs';

/** Moves one song, and its file, to another library. */
export function MoveSongModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title="Move to library">
      {/* Keyed by the song, and unmounted with the dialog, so one song's error never reaches the next. */}
      <MoveSongForm key={song?.id} song={song} onClose={onClose} />
    </Modal>
  );
}

function MoveSongForm({ song, onClose }: { song: SongResource | null; onClose: () => void }) {
  const libraries = useLibraries();
  const move = useMoveSongs();
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCommand(commandId, { poll: true });

  // The file is in the other library only once the command has run, not when the API answered 202.
  useRefreshSongsWhenFinished(command.data);

  const currentId = song === null ? null : String(song.libraryId);
  const list = libraries.data ?? [];

  const submit = () => {
    if (song === null || libraryId === null) {
      return;
    }

    move.mutate(
      { songIds: [Number(song.id)], libraryId: Number(libraryId) },
      { onSuccess: (accepted) => setCommandId(Number(accepted.commandId)) },
    );
  };

  return (
    <Stack gap="md">
      <Text size="sm">
        Move “{song?.title ?? ''}” to another library? Its file is moved and re-tagged for the library it lands in.
      </Text>

      <Select
        label="Library"
        placeholder="Pick a library"
        data={list.map((library) => ({
          value: String(library.id),
          label: library.id.toString() === currentId ? `${library.name} (current)` : library.name,
        }))}
        value={libraryId}
        disabled={list.length < 2}
        onChange={setLibraryId}
      />

      {move.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {move.error.message}
        </Alert>
      )}

      {command.data?.message !== null && command.data?.message !== undefined && (
        <Text size="sm">{command.data.message}</Text>
      )}

      {command.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {command.error.message}
        </Alert>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Close
        </Button>
        <Button disabled={libraryId === null} loading={move.isPending} onClick={submit}>
          Move
        </Button>
      </Group>
    </Stack>
  );
}
