import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  CopyButton,
  Group,
  Menu,
  Modal,
  Radio,
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
import { firstPage, type Paging } from '../../api/paging';
import { useLibraries, ValidationError, type LibraryResource } from '../../api/profiles';
import {
  useAddReferenceLibrary,
  useAdoptReferenceLibraries,
  useDeleteReferenceLibrary,
  useReferenceLibraries,
  useReferenceLibraryOwnedSongs,
  useReplacedReferenceFiles,
  useScanReferenceLibrary,
  useUpdateReferenceLibrary,
  type ReferenceLibraryDeleteChoice,
  type ReferenceLibraryInput,
  type ReferenceLibraryModeName,
  type ReferenceLibraryResource,
  type ReplacedReferenceFileResource,
} from '../../api/references';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { PagedTable, type PagedColumn } from '../../components/PagedTable';

/**
 * Settings → Reference libraries (LIBRARY_OUTPUT §7.6): the folders the user already has, what the
 * scan found in them, the two commands that walk or adopt one, and the files a library copy replaced
 * (DECISIONS build session 14 #32).
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
  const replaces = library.replaceBelowCutoff ? ' · replaces files below the cutoff' : '';

  if (library.mode !== 'adopt') {
    return `Reference only${replaces}`;
  }

  const id = library.libraryId === null ? null : Number(library.libraryId);

  return `Adopt into ${id === null ? 'no library' : (names.get(id) ?? `library #${id}`)}${replaces}`;
}

/** Why a file is flagged, in the words the badges' tooltips use. */
const RIP_EXPLANATION =
  'Lossy at 48 kHz, the rate YouTube serves its audio at; CDs and download stores are 44.1 kHz. They are rated OPUS-160, like a YouTube download.';

/** Why a replaced file whose library copy is worse is left out of the copied paths. */
const KEEP_EXPLANATION =
  'The song had a worse library copy before this file was found, so this file is the one to keep. It is left out of the copied paths.';

/** The anchor the "replaced" badge scrolls to. */
const REPLACED_ANCHOR = 'replaced-files';

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

      {Number(counts.likelyYouTubeRips) > 0 && (
        <Tooltip label={RIP_EXPLANATION} multiline w={320} events={{ hover: true, focus: true, touch: true }}>
          <Badge color="grape" variant="light" size="sm" tabIndex={0}>
            {Number(counts.likelyYouTubeRips)} likely YouTube {Number(counts.likelyYouTubeRips) === 1 ? 'rip' : 'rips'}
          </Badge>
        </Tooltip>
      )}

      {Number(counts.replaced) > 0 && (
        <a href={`#${REPLACED_ANCHOR}`}>
          <Badge color="blue" variant="light" size="sm">
            {Number(counts.replaced)} replaced
          </Badge>
        </a>
      )}
    </Group>
  );
}

/** `3 Oct 2026`, short enough for a table cell. */
function formatDay(iso: string): string {
  const parsed = new Date(iso);

  return Number.isNaN(parsed.getTime())
    ? '—'
    : parsed.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}

/**
 * The files a library copy has replaced: their songs hold a file in the managed library now, so nothing
 * uses them. Wondarr never deletes them; the paths can be copied to delete them by hand.
 */
function ReplacedFilesCard() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const query = useReplacedReferenceFiles(paging);

  const rows = query.data?.records ?? [];
  const total = Number(query.data?.totalRecords ?? 0);
  // A file that rates higher than its library copy is the one to keep, so its path is never copied.
  const deletable = rows.filter((file) => !file.libraryCopyRatesLower);

  if (total === 0 && query.error === null) {
    return null;
  }

  const columns: PagedColumn<ReplacedReferenceFileResource>[] = [
    {
      label: 'Your file',
      sortKey: null,
      render: (file) => (
        <Stack gap={2}>
          <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
            {file.relativePath}
          </Text>
          <Group gap={4}>
            <Text size="xs" c="dimmed">
              {file.referenceLibraryName}
              {file.quality === null ? '' : ` · ${file.quality}`}
            </Text>
            {file.likelyYouTubeRip && (
              <Tooltip label={RIP_EXPLANATION} multiline w={320} events={{ hover: true, focus: true, touch: true }}>
                <Badge color="grape" variant="light" size="xs" tabIndex={0}>
                  Likely YouTube rip
                </Badge>
              </Tooltip>
            )}
            {file.libraryCopyRatesLower && (
              <Tooltip label={KEEP_EXPLANATION} multiline w={320} events={{ hover: true, focus: true, touch: true }}>
                <Badge color="orange" variant="light" size="xs" tabIndex={0}>
                  Your file rates higher
                </Badge>
              </Tooltip>
            )}
          </Group>
        </Stack>
      ),
    },
    {
      label: 'Song',
      sortKey: null,
      render: (file) => (
        <Link to={`/song/${String(file.songId)}`}>
          <Text size="sm">
            {file.artistCredit} – {file.title}
          </Text>
        </Link>
      ),
    },
    {
      label: 'Library copy',
      sortKey: null,
      render: (file) => (
        <Tooltip label={file.libraryPath} multiline w={420}>
          <Text size="sm">{file.libraryQuality === '' ? '—' : file.libraryQuality}</Text>
        </Tooltip>
      ),
    },
    { label: 'Since', sortKey: null, width: 120, render: (file) => formatDay(file.libraryFileSince) },
  ];

  return (
    <Card withBorder padding="md" id={REPLACED_ANCHOR}>
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start">
          <Stack gap={2}>
            <Title order={4}>Replaced by a library copy</Title>
            <Text size="sm" c="dimmed">
              These songs now use a file in your library, so nothing uses these files any more. Wondarr never deletes
              your files; delete them yourself when you are happy with the copies. A deleted file leaves this list a day
              later. The copied paths are as the container sees them, and leave out the files that rate higher than
              their library copy.
            </Text>
          </Stack>
          <CopyButton value={deletable.map((file) => file.path).join('\n')}>
            {({ copied, copy }) => (
              <Button variant="light" size="xs" onClick={copy} disabled={deletable.length === 0}>
                {copied ? 'Copied' : 'Copy the paths on this page'}
              </Button>
            )}
          </CopyButton>
        </Group>

        <PagedTable
          columns={columns}
          rows={rows}
          totalRecords={total}
          paging={paging}
          onPaging={setPaging}
          isLoading={query.isPending}
          error={query.error}
          emptyMessage="No file has been replaced."
          rowKey={(file) => file.id}
          compact
        />
      </Stack>
    </Card>
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
  const [replaceBelowCutoff, setReplaceBelowCutoff] = useState(library?.replaceBelowCutoff ?? false);
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

    const input: ReferenceLibraryInput = { name, rootPath, mode, libraryId, enabled, replaceBelowCutoff };
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

      <Switch
        label="Replace files below the cutoff"
        description="A song whose file here is below its quality profile's cutoff is upgraded like a library song: the next Upgrade Search looks for a better copy, which goes into your library. Likely YouTube rips count as OPUS-160. Your file is never touched; it shows under Replaced by a library copy, for you to delete."
        checked={replaceBelowCutoff}
        error={fieldError('replaceBelowCutoff')}
        onChange={(event) => setReplaceBelowCutoff(event.currentTarget.checked)}
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

/**
 * What the delete dialog offers for the songs the library owned. The default is `wanted`: the songs are
 * not lost and nothing starts searching behind the user's back; only `search` starts a search at once.
 */
const DELETE_CHOICES: { value: ReferenceLibraryDeleteChoice; label: string }[] = [
  { value: 'wanted', label: 'Keep them wanted; the scheduled search will pick them up' },
  { value: 'search', label: 'Make them wanted and search every wanted song now' },
  { value: 'unmonitor', label: 'Unmonitor them' },
];

/** The body of the delete dialog: how many songs it affects, and what to do with them. */
function DeleteLibraryContent({ library, onClose }: { library: ReferenceLibraryResource; onClose: () => void }) {
  const owned = useReferenceLibraryOwnedSongs(Number(library.id));
  const remove = useDeleteReferenceLibrary();
  const [choice, setChoice] = useState<ReferenceLibraryDeleteChoice>('wanted');

  const count = owned.data;

  return (
    <Stack gap="md">
      {owned.isPending && <Text size="sm">Counting the songs this folder owns…</Text>}

      {owned.isError && (
        <Stack gap="xs">
          <Text size="sm" c="red">
            {owned.error.message}
          </Text>
          <Text size="sm">You can still delete the library. Your files are not touched.</Text>
          <Group>
            <Button size="xs" variant="default" loading={owned.isFetching} onClick={() => void owned.refetch()}>
              Retry
            </Button>
          </Group>
        </Stack>
      )}

      {count !== undefined && (
        <Text size="sm">
          {count === 0
            ? 'No song is owned through this folder. '
            : `${count} ${count === 1 ? 'song is' : 'songs are'} owned through this folder; deleting it takes ${count === 1 ? 'its' : 'their'} file away. `}
          Your files are not touched.
        </Text>
      )}

      {((count !== undefined && count > 0) || owned.isError) && (
        <Radio.Group
          label="What should happen to those songs?"
          description="Only the second choice starts searching right away, and it searches every wanted song, not only these."
          value={choice}
          onChange={setChoice}
        >
          <Stack gap="xs" mt="xs">
            {DELETE_CHOICES.map((option) => (
              <Radio key={option.value} value={option.value} label={option.label} />
            ))}
          </Stack>
        </Radio.Group>
      )}

      {remove.error !== null && (
        <Text size="sm" c="red">
          {remove.error.message}
        </Text>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          color="red"
          loading={remove.isPending}
          disabled={owned.isPending}
          onClick={() => remove.mutate({ id: Number(library.id), songs: choice }, { onSuccess: onClose })}
        >
          Delete
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
          <Table.ScrollContainer minWidth={560}>
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
          </Table.ScrollContainer>
        </Card>
      )}

      <ReplacedFilesCard />

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
        {pendingDelete !== null && (
          <DeleteLibraryContent key={pendingDelete.id} library={pendingDelete} onClose={() => setPendingDelete(null)} />
        )}
      </Modal>
    </Stack>
  );
}
