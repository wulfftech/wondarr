import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Menu,
  Modal,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, EllipsisVertical, Plus } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { useLibraries, ValidationError, type LibraryResource } from '../../api/profiles';
import {
  useAddReferenceLibrary,
  useAdoptReferenceLibraries,
  useDeleteReferenceLibrary,
  useReferenceLibraries,
  useScanReferenceLibrary,
  useUpdateReferenceLibrary,
  type ReferenceLibraryInput,
  type ReferenceLibraryModeName,
  type ReferenceLibraryResource,
} from '../../api/references';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

/**
 * Settings → Reference libraries (LIBRARY_OUTPUT §7.6): the folders the user already has, what the
 * scan found in them, and the two commands that walk or adopt one.
 */

/** The two modes a reference library can be in, as the form offers them. */
const MODE_OPTIONS: { value: ReferenceLibraryModeName; label: string }[] = [
  { value: 'reference', label: 'Reference' },
  { value: 'adopt', label: 'Adopt' },
];

/** What each state's badge says and how it reads at a glance. */
const COUNT_BADGES: { key: keyof ReferenceLibraryResource['counts']; label: string; color: string }[] = [
  { key: 'identified', label: 'identified', color: 'green' },
  { key: 'adopted', label: 'adopted', color: 'teal' },
  { key: 'unreadable', label: 'unreadable', color: 'orange' },
  { key: 'missing', label: 'missing', color: 'red' },
];

/**
 * How long ago a scan finished, short enough for a table cell. The moment is the server's clock, so
 * a clock that disagrees by a second or two reads as "0s" rather than a negative age.
 */
function formatAge(iso: string | null): string {
  if (iso === null || iso === '') {
    return 'never';
  }

  const parsed = new Date(iso);

  if (Number.isNaN(parsed.getTime())) {
    return 'never';
  }

  const seconds = Math.max(0, Math.round((Date.now() - parsed.getTime()) / 1000));

  if (seconds < 60) {
    return `${seconds}s ago`;
  }

  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)}m ago`;
  }

  if (seconds < 86400) {
    return `${Math.floor(seconds / 3600)}h ago`;
  }

  return `${Math.floor(seconds / 86400)}d ago`;
}

/** How the library's mode reads, naming the library adopted songs are filed under. */
function modeText(library: ReferenceLibraryResource, names: Map<number, string>): string {
  if (library.mode !== 'adopt') {
    return 'Reference only';
  }

  const id = library.libraryId === null ? null : Number(library.libraryId);

  return `Adopt into ${id === null ? 'no library' : (names.get(id) ?? `library #${id}`)}`;
}

/** The counts as small badges; "needs review" links to the Match queue filtered to this library. */
function CountsCell({ library }: { library: ReferenceLibraryResource }) {
  const counts = library.counts;
  const needsReview = Number(counts.ambiguous) + Number(counts.unmatched);

  return (
    <Group gap={4}>
      {COUNT_BADGES.map(({ key, label, color }) => {
        const value = Number(counts[key]);

        return value === 0 ? null : (
          <Badge key={key} color={color} variant="light" size="sm">
            {value} {label}
          </Badge>
        );
      })}

      {needsReview > 0 && (
        <Link to={`/match?referenceLibraryId=${String(library.id)}`}>
          <Badge color="yellow" variant="light" size="sm">
            {needsReview} need review
          </Badge>
        </Link>
      )}
    </Group>
  );
}

/** The add/edit form's own state, reset every time the modal opens. */
function LibraryForm({ library, onClose }: { library: ReferenceLibraryResource | null; onClose: () => void }) {
  const libraries = useLibraries();
  const add = useAddReferenceLibrary();
  const update = useUpdateReferenceLibrary();

  const [name, setName] = useState(library?.name ?? '');
  const [rootPath, setRootPath] = useState(library?.rootPath ?? '');
  const [mode, setMode] = useState<ReferenceLibraryModeName>(library?.mode === 'adopt' ? 'adopt' : 'reference');
  const [libraryId, setLibraryId] = useState<number | null>(
    library?.libraryId === null || library?.libraryId === undefined ? null : Number(library.libraryId),
  );
  const [enabled, setEnabled] = useState(library?.enabled ?? true);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});

  const save = library === null ? add : update;

  // The server names the field it refused with, in the resource's own camelCase spelling.
  const serverFields = save.error instanceof ValidationError ? save.error.fields : {};
  const fieldError = (field: string) => clientErrors[field] ?? serverFields[field];

  const libraryOptions = (libraries.data ?? []).map((candidate: LibraryResource) => ({
    value: String(candidate.id),
    label: candidate.name,
  }));

  const submit = () => {
    const errors: Record<string, string> = {};

    if (name.trim() === '') {
      errors.name = 'name must not be empty.';
    }

    if (rootPath.trim() === '') {
      errors.rootPath = 'rootPath must not be empty.';
    }

    if (mode === 'adopt' && libraryId === null) {
      errors.libraryId = 'libraryId is required in adopt mode.';
    }

    setClientErrors(errors);

    if (Object.keys(errors).length > 0) {
      return;
    }

    const input: ReferenceLibraryInput = { name, rootPath, mode, libraryId, enabled };
    const done = () => {
      notifications.show({ message: library === null ? 'Reference library added' : 'Saved', color: 'green' });
      onClose();
    };

    if (library === null) {
      add.mutate(input, { onSuccess: done });
    } else {
      update.mutate({ ...input, id: Number(library.id) }, { onSuccess: done });
    }
  };

  return (
    <Stack gap="md">
      <TextInput
        label="Name"
        value={name}
        error={fieldError('name')}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      <TextInput
        label="Root path"
        description="The folder to walk, as the container sees it."
        value={rootPath}
        error={fieldError('rootPath')}
        onChange={(event) => setRootPath(event.currentTarget.value)}
      />

      <Stack gap={4}>
        <Text size="sm" fw={500}>
          Mode
        </Text>
        <SegmentedControl value={mode} onChange={setMode} data={MODE_OPTIONS} />
      </Stack>

      <Select
        label="Target library"
        description={
          mode === 'adopt'
            ? 'Adopted files are copied into this library.'
            : 'Songs found here are filed under this library.'
        }
        data={libraryOptions}
        value={libraryId === null ? null : String(libraryId)}
        error={fieldError('libraryId')}
        onChange={(value) => setLibraryId(value === null ? null : Number(value))}
      />

      <Switch
        label="Scan daily"
        checked={enabled}
        error={fieldError('enabled')}
        onChange={(event) => setEnabled(event.currentTarget.checked)}
      />

      {save.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {save.error.message}
        </Alert>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button loading={save.isPending} onClick={submit}>
          Save
        </Button>
      </Group>
    </Stack>
  );
}

/** The list of reference libraries and the dialogs that add, edit and delete one. */
export function ReferenceLibrariesPage() {
  const query = useReferenceLibraries();
  const libraries = useLibraries();
  const scan = useScanReferenceLibrary();
  const adopt = useAdoptReferenceLibraries();
  const remove = useDeleteReferenceLibrary();

  const [form, setForm] = useState<{ library: ReferenceLibraryResource | null } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ReferenceLibraryResource | null>(null);

  const names = new Map((libraries.data ?? []).map((library) => [Number(library.id), library.name]));

  const addButton = (
    <Button leftSection={<Plus size={16} />} onClick={() => setForm({ library: null })}>
      Add reference library
    </Button>
  );

  const rows = query.data ?? [];

  return (
    <Stack gap="lg">
      <Title order={3}>Reference libraries</Title>

      <Text size="sm" c="dimmed">
        A reference library is a folder you already have: Wondarr scans it and counts the songs it finds, leaving the
        files exactly where they are. In adopt mode the identified files are copied into the library you pick — the
        originals are never touched.
      </Text>

      <Group justify="flex-end">{addButton}</Group>

      {query.isPending && <LoadingState />}

      {query.error !== null && <ErrorState message={query.error.message} />}

      {!query.isPending && query.error === null && rows.length === 0 && (
        <EmptyState message="No reference libraries yet" />
      )}

      {rows.length > 0 && (
        <Card withBorder padding="md">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th>Root path</Table.Th>
                <Table.Th w={200}>Mode</Table.Th>
                <Table.Th w={90}>Enabled</Table.Th>
                <Table.Th w={180}>Last scan</Table.Th>
                <Table.Th>Files</Table.Th>
                <Table.Th w={50} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((library) => (
                <Table.Tr key={library.id}>
                  <Table.Td>{library.name}</Table.Td>
                  <Table.Td>
                    <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
                      {library.rootPath}
                    </Text>
                  </Table.Td>
                  <Table.Td>{modeText(library, names)}</Table.Td>
                  <Table.Td>{library.enabled ? 'Yes' : 'No'}</Table.Td>
                  <Table.Td>
                    <Tooltip
                      label={library.lastScanMessage ?? 'This library has not been scanned yet.'}
                      multiline
                      w={320}
                    >
                      <Text size="sm">{formatAge(library.lastScannedAt)}</Text>
                    </Tooltip>
                  </Table.Td>
                  <Table.Td>
                    <CountsCell library={library} />
                  </Table.Td>
                  <Table.Td>
                    <Menu withinPortal>
                      <Menu.Target>
                        <ActionIcon
                          variant="subtle"
                          aria-label={`Actions for ${library.name}`}
                          onClick={(event) => event.stopPropagation()}
                        >
                          <EllipsisVertical size={16} />
                        </ActionIcon>
                      </Menu.Target>
                      <Menu.Dropdown>
                        <Menu.Item
                          onClick={() =>
                            scan.mutate(Number(library.id), {
                              onSuccess: () => notifications.show({ message: 'Scan queued', color: 'green' }),
                              onError: (error) => notifications.show({ message: error.message, color: 'red' }),
                            })
                          }
                        >
                          Scan now
                        </Menu.Item>
                        {library.mode === 'adopt' && (
                          <Menu.Item
                            onClick={() =>
                              adopt.mutate(Number(library.id), {
                                onSuccess: () => notifications.show({ message: 'Adopt queued', color: 'green' }),
                                onError: (error) => notifications.show({ message: error.message, color: 'red' }),
                              })
                            }
                          >
                            Adopt now
                          </Menu.Item>
                        )}
                        <Menu.Item onClick={() => setForm({ library })}>Edit</Menu.Item>
                        <Menu.Item color="red" onClick={() => setPendingDelete(library)}>
                          Delete
                        </Menu.Item>
                      </Menu.Dropdown>
                    </Menu>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      <Modal
        opened={form !== null}
        onClose={() => setForm(null)}
        title={form?.library == null ? 'Add a reference library' : 'Edit reference library'}
      >
        {form !== null && (
          <LibraryForm key={form.library?.id ?? 'new'} library={form.library} onClose={() => setForm(null)} />
        )}
      </Modal>

      <Modal opened={pendingDelete !== null} onClose={() => setPendingDelete(null)} title="Delete reference library">
        <Stack gap="md">
          <Text size="sm">Songs owned only through this folder become wanted again. Your files are not touched.</Text>

          {remove.error !== null && (
            <Text size="sm" c="red">
              {remove.error.message}
            </Text>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPendingDelete(null)}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={() => {
                if (pendingDelete !== null) {
                  remove.mutate(Number(pendingDelete.id), { onSuccess: () => setPendingDelete(null) });
                }
              }}
            >
              Delete
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
