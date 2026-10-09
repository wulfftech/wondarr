import { Alert, Button, Group, Modal, Paper, Radio, Select, Stack, TagsInput, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import type { LibraryResource, QualityProfileResource } from '../../api/profiles';
import { readEnum } from '../../api/profiles';
import { useCommand, useDeleteSongs, useEditSongs, type ApplyTags, type CommandStatusName } from '../../api/songs';

/** The sticky bar that appears while songs are selected: change or delete them all at once. */

interface MassEditorBarProps {
  selectedIds: number[];
  profiles: QualityProfileResource[];
  libraries: LibraryResource[];
  /** The tags songs already carry, offered as suggestions. */
  knownTags: string[];
  onClear: () => void;
}

const APPLY_MODES: { value: ApplyTags; label: string }[] = [
  { value: 'add', label: 'Add to the songs’ tags' },
  { value: 'remove', label: 'Remove from the songs’ tags' },
  { value: 'replace', label: 'Replace the songs’ tags' },
];

function songs(count: number): string {
  return count === 1 ? '1 song' : `${count} songs`;
}

/** Follows the first of the move commands a library change queued. */
function MoveProgress({ commandId, count }: { commandId: number; count: number }) {
  const command = useCommand(commandId, { poll: true });
  const status = readEnum<CommandStatusName>(command.data?.status);

  if (status === 'completed') {
    return <Text size="sm">Moved {songs(count)}.</Text>;
  }

  if (status === 'failed' || status === 'aborted') {
    return (
      <Text size="sm" c="red">
        {command.data?.message ?? 'The move failed.'}
      </Text>
    );
  }

  return <Text size="sm">Moving {songs(count)}…</Text>;
}

function TagsModal({
  opened,
  selectedIds,
  knownTags,
  onClose,
}: {
  opened: boolean;
  selectedIds: number[];
  knownTags: string[];
  onClose: () => void;
}) {
  const edit = useEditSongs();
  const [tags, setTags] = useState<string[]>([]);
  const [mode, setMode] = useState<ApplyTags>('add');

  const close = () => {
    edit.reset();
    onClose();
  };

  const apply = () => {
    edit.mutate(
      { songIds: selectedIds, tags, applyTags: mode },
      {
        onSuccess: () => {
          setTags([]);
          setMode('add');
          close();
        },
      },
    );
  };

  return (
    <Modal opened={opened} onClose={close} title={`Tags for ${songs(selectedIds.length)}`}>
      <Stack gap="md">
        <TagsInput
          label="Tags"
          placeholder="Pick or type a tag"
          data={knownTags}
          value={tags}
          onChange={setTags}
          clearable
        />

        <Radio.Group value={mode} onChange={setMode}>
          <Stack gap="xs">
            {APPLY_MODES.map((option) => (
              <Radio key={option.value} value={option.value} label={option.label} />
            ))}
          </Stack>
        </Radio.Group>

        {edit.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {edit.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            Cancel
          </Button>
          <Button disabled={tags.length === 0 && mode !== 'replace'} loading={edit.isPending} onClick={apply}>
            Apply
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function DeleteSongsModal({
  opened,
  selectedIds,
  onClose,
  onDeleted,
}: {
  opened: boolean;
  selectedIds: number[];
  onClose: () => void;
  onDeleted: () => void;
}) {
  const remove = useDeleteSongs();

  const close = () => {
    remove.reset();
    onClose();
  };

  return (
    <Modal opened={opened} onClose={close} title="Delete songs">
      <Stack gap="md">
        <Text size="sm">Delete {songs(selectedIds.length)} from Wondarr? Their files stay on disk.</Text>

        {remove.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {remove.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            Cancel
          </Button>
          <Button
            color="red"
            loading={remove.isPending}
            onClick={() =>
              remove.mutate(selectedIds, {
                onSuccess: () => {
                  close();
                  onDeleted();
                },
              })
            }
          >
            Delete
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function MassEditorBar({ selectedIds, profiles, libraries, knownTags, onClear }: MassEditorBarProps) {
  const edit = useEditSongs();
  const [profileId, setProfileId] = useState<string | null>(null);
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<'tags' | 'delete' | null>(null);
  const [moving, setMoving] = useState<{ commandId: number; count: number } | null>(null);

  const count = selectedIds.length;

  if (count === 0) {
    return null;
  }

  const monitor = (monitored: boolean) => edit.mutate({ songIds: selectedIds, monitored });

  return (
    <>
      <Paper withBorder shadow="md" p="sm" pos="sticky" bottom={16} style={{ zIndex: 10 }}>
        <Stack gap="xs">
          <Group gap="sm" align="flex-end">
            <Text fw={600} size="sm" pb={6}>
              {count} selected
            </Text>

            <Button variant="default" size="xs" disabled={edit.isPending} onClick={() => monitor(true)}>
              Monitor
            </Button>
            <Button variant="default" size="xs" disabled={edit.isPending} onClick={() => monitor(false)}>
              Unmonitor
            </Button>

            <Group gap={4} align="flex-end">
              <Select
                aria-label="Quality profile for the selection"
                placeholder="Quality profile"
                size="xs"
                w={170}
                data={profiles.map((profile) => ({ value: String(profile.id), label: profile.name }))}
                value={profileId}
                onChange={setProfileId}
              />
              <Button
                variant="default"
                size="xs"
                disabled={profileId === null || edit.isPending}
                onClick={() => {
                  if (profileId !== null) {
                    edit.mutate({ songIds: selectedIds, qualityProfileId: Number(profileId) });
                  }
                }}
              >
                Apply profile
              </Button>
            </Group>

            {libraries.length > 1 && (
              <Group gap={4} align="flex-end">
                <Select
                  aria-label="Library for the selection"
                  placeholder="Library"
                  size="xs"
                  w={150}
                  data={libraries.map((library) => ({ value: String(library.id), label: library.name }))}
                  value={libraryId}
                  onChange={setLibraryId}
                />
                <Button
                  variant="default"
                  size="xs"
                  disabled={libraryId === null || edit.isPending}
                  onClick={() => {
                    if (libraryId !== null) {
                      edit.mutate(
                        { songIds: selectedIds, libraryId: Number(libraryId) },
                        {
                          onSuccess: (result) => {
                            const first = result.moveCommandIds[0];
                            setMoving(first === undefined ? null : { commandId: Number(first), count });
                          },
                        },
                      );
                    }
                  }}
                >
                  Apply library
                </Button>
              </Group>
            )}

            <Button variant="default" size="xs" onClick={() => setDialog('tags')}>
              Tags…
            </Button>
            <Button color="red" variant="light" size="xs" onClick={() => setDialog('delete')}>
              Delete…
            </Button>
            <Button variant="subtle" size="xs" onClick={onClear}>
              Clear selection
            </Button>
          </Group>

          {moving !== null && <MoveProgress commandId={moving.commandId} count={moving.count} />}

          {edit.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {edit.error.message}
            </Alert>
          )}
        </Stack>
      </Paper>

      <TagsModal
        opened={dialog === 'tags'}
        selectedIds={selectedIds}
        knownTags={knownTags}
        onClose={() => setDialog(null)}
      />

      <DeleteSongsModal
        opened={dialog === 'delete'}
        selectedIds={selectedIds}
        onClose={() => setDialog(null)}
        onDeleted={onClear}
      />
    </>
  );
}
