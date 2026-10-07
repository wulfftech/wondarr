import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  FileInput,
  Group,
  Modal,
  NumberInput,
  Progress,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, Plus } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  IMPORT_LIST_POLICIES,
  IMPORT_LIST_POLICY_HELP,
  useCreateImportList,
  useCsvPreview,
  useDeleteImportList,
  useImportListSchema,
  useImportLists,
  useSyncImportList,
  useUpdateImportList,
  type CsvPreviewResource,
  type ImportListInput,
  type ImportListSchemaResource,
} from '../../api/importLists';
import { useLibraries, useQualityProfiles, readEnum, ValidationError } from '../../api/profiles';
import {
  useCommand,
  type CommandStatusName,
  type ImportListResource,
} from '../../api/songs';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { formatDuration } from '../../components/SongCells';
import { initialValues, SettingsField, wireValue } from '../../components/SchemaFields';

/**
 * Settings → Import lists (ARCHITECTURE §5.8): the sources Wondarr watches — a playlist, an uploaded
 * CSV — with the policy each one applies, the interval it is read on, and the playlist outputs it
 * writes. The settings form is rendered from the server's schema, so a provider added there appears
 * here without a change to this page.
 */

/** Reads a resource's settings node as the object the provider's fields name. */
function asSettings(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

/** The last sync as the table shows it, or the word for a list that has never been read. */
function formatSynced(value: string | null): string {
  if (value === null) {
    return 'Never';
  }

  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/** What a preview's `format` code reads as. */
function formatLabel(format: string): string {
  return format === 'exportify' ? 'Exportify export' : 'Mapped CSV';
}

/** What a progress message such as "Resolved 12 of 50 lines" says, or `null` when it says nothing. */
function parseProgress(message: string | null | undefined): { done: number; total: number } | null {
  const match = /(\d+)\s+of\s+(\d+)/.exec(message ?? '');

  return match === null ? null : { done: Number(match[1]), total: Number(match[2]) };
}

/** The command a **Sync now** started: its progress while it runs, its message once it stops. */
function SyncProgress({
  commandId,
  name,
  onFinished,
}: {
  commandId: number;
  name: string;
  onFinished: () => void;
}) {
  const command = useCommand(commandId, { poll: true });
  const told = useRef(false);

  const status = readEnum<CommandStatusName>(command.data?.status);
  const running = command.data !== undefined && status !== 'completed' && status !== 'failed' && status !== 'aborted';
  const progress = parseProgress(command.data?.message);

  useEffect(() => {
    if (command.data !== undefined && !running && !told.current) {
      told.current = true;
      onFinished();
    }
  }, [command.data, running, onFinished]);

  return (
    <Card withBorder padding="md">
      <Stack gap="sm">
        <Text fw={600}>{running ? `Syncing ${name}…` : `Synced ${name}`}</Text>

        {command.data?.message !== null && command.data?.message !== undefined && (
          <Text size="sm" c={command.data.message.startsWith('Failed:') ? 'red' : undefined}>
            {command.data.message}
          </Text>
        )}

        {progress !== null && (
          <Progress
            value={progress.total === 0 ? 0 : Math.round((progress.done / progress.total) * 100)}
            aria-label="Sync progress"
          />
        )}

        {command.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {command.error.message}
          </Alert>
        )}
      </Stack>
    </Card>
  );
}

/** The add/edit dialog: the name, the policy, the outputs, and the settings the schema asks for. */
function ImportListForm({
  list,
  provider,
  onClose,
}: {
  list: ImportListResource | null;
  provider: ImportListSchemaResource;
  onClose: () => void;
}) {
  const create = useCreateImportList();
  const update = useUpdateImportList();
  const profiles = useQualityProfiles();
  const libraries = useLibraries();
  const save = list === null ? create : update;

  const [initial] = useState(() => initialValues(provider.fields, list === null ? {} : asSettings(list.settings)));
  const masked = initial.masked;

  const [name, setName] = useState(list?.name ?? '');
  const [enabled, setEnabled] = useState(list?.enabled ?? true);
  const [values, setValues] = useState<Record<string, unknown>>(initial.values);
  const [policy, setPolicy] = useState<string>(list?.policy ?? 'AddOnly');
  const [profileId, setProfileId] = useState<string | null>(
    list === null || Number(list.qualityProfileId) === 0 ? null : String(list.qualityProfileId),
  );
  const [libraryId, setLibraryId] = useState<string | null>(
    list === null || Number(list.libraryId) === 0 ? null : String(list.libraryId),
  );
  const [interval, setInterval] = useState<number | string>(list === null ? 24 : Number(list.syncIntervalHours));
  const [plexPlaylist, setPlexPlaylist] = useState(list?.plexPlaylist ?? false);
  const [m3uExport, setM3uExport] = useState(list?.m3uExport ?? false);
  const [sourceText, setSourceText] = useState<string | null>(null);
  const [preview, setPreview] = useState<CsvPreviewResource | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const isCsv = provider.type === 'csv';

  const serverFields = save.error instanceof ValidationError ? save.error.fields : {};
  const general = Object.entries(serverFields).filter(
    ([field]) =>
      field !== 'name' &&
      field !== 'settings' &&
      field !== 'sourceText' &&
      field !== 'syncIntervalHours' &&
      field !== 'qualityProfileId' &&
      field !== 'libraryId' &&
      field !== 'type' &&
      !provider.fields.some((one) => one.name === field),
  );

  /** The provider's settings as the JSON body carries them. */
  const mapping = useCallback((): Record<string, unknown> => {
    const settings: Record<string, unknown> = {};

    for (const field of provider.fields) {
      settings[field.name] = wireValue(field, values[field.name], masked.has(field.name));
    }

    return settings;
  }, [provider.fields, values, masked]);

  const { mutate: runPreview } = useCsvPreview();
  const previewedText = useRef<string | null>(null);

  // The first read of a file runs at once; a change to the column mapping is debounced, so typing a
  // header does not read the file after every key.
  useEffect(() => {
    if (!isCsv || sourceText === null) {
      return undefined;
    }

    const delay = previewedText.current === sourceText ? 400 : 0;
    previewedText.current = sourceText;
    const settings = mapping();
    const handle = window.setTimeout(() => {
      runPreview(
        { sourceText, settings },
        {
          onSuccess: (result) => {
            setPreview(result);
            setPreviewError(null);
          },
          onError: (error) => setPreviewError(error.message),
        },
      );
    }, delay);

    return () => window.clearTimeout(handle);
  }, [isCsv, sourceText, mapping, runPreview]);

  const basic = provider.fields.filter((field) => !field.advanced);
  const advanced = provider.fields.filter((field) => field.advanced);
  // Exportify's columns are known, so the mapping fields would only be in the way; a generic file is
  // mapped by hand, and so is a file that has not been read yet.
  const showColumns = !isCsv || preview === null || preview.format === 'generic';

  const setValue = (field: (typeof provider.fields)[number], next: unknown) =>
    setValues((current) => ({ ...current, [field.name]: next }));

  const input = (): ImportListInput => ({
    type: provider.type,
    name,
    settings: mapping(),
    policy,
    qualityProfileId: profileId === null ? null : Number(profileId),
    libraryId: libraryId === null ? null : Number(libraryId),
    enabled,
    syncIntervalHours: interval === '' ? null : Number(interval),
    plexPlaylist,
    m3uExport,
    ...(isCsv && sourceText !== null ? { sourceText } : {}),
  });

  const submit = () => {
    const done = () => {
      notifications.show({ message: list === null ? 'List added' : 'Saved', color: 'green' });
      onClose();
    };

    if (list === null) {
      create.mutate(input(), { onSuccess: done });
    } else {
      update.mutate({ ...input(), id: Number(list.id) }, { onSuccess: done });
    }
  };

  return (
    <Stack gap="md">
      <TextInput
        label="Name"
        value={name}
        error={serverFields.name}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      {basic.map(
        (field) =>
          showColumns && (
            <SettingsField
              key={field.name}
              field={field}
              value={values[field.name]}
              masked={masked.has(field.name)}
              error={serverFields[field.name]}
              onChange={(next) => setValue(field, next)}
            />
          ),
      )}

      {advanced.length > 0 &&
        showColumns && (
          <details>
            <summary>Advanced</summary>
            <Stack gap="md" mt="sm">
              {advanced.map((field) => (
                <SettingsField
                  key={field.name}
                  field={field}
                  value={values[field.name]}
                  masked={masked.has(field.name)}
                  error={serverFields[field.name]}
                  onChange={(next) => setValue(field, next)}
                />
              ))}
            </Stack>
          </details>
        )}

      {isCsv && (
        <Stack gap="xs">
          <FileInput
            label="CSV file"
            description={
              list === null
                ? 'The file is read here; only its text is sent.'
                : 'Choosing a file replaces the one the list stores.'
            }
            accept=".csv,text/csv"
            clearable={list !== null}
            onChange={(file) => {
              setPreview(null);
              setSourceText(null);

              if (file !== null) {
                void file.text().then((text) => setSourceText(text));
              }
            }}
          />

          {serverFields.sourceText !== undefined && (
            <Text size="sm" c="red">
              {serverFields.sourceText}
            </Text>
          )}

          {previewError !== null && (
            <Text size="sm" c="red">
              {previewError}
            </Text>
          )}

          {preview !== null && (
            <Stack gap="xs">
              <Group gap="sm">
                <Badge variant="light">{formatLabel(preview.format)}</Badge>
                <Text size="sm">
                  {String(preview.rowCount)} {Number(preview.rowCount) === 1 ? 'row' : 'rows'}
                </Text>
              </Group>

              {preview.problems.map((problem) => (
                <Text key={problem} size="sm" c="red">
                  {problem}
                </Text>
              ))}

              {preview.sample.length > 0 && (
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Title</Table.Th>
                      <Table.Th>Artist</Table.Th>
                      <Table.Th>ISRC</Table.Th>
                      <Table.Th w={90}>Length</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {preview.sample.map((entry) => (
                      <Table.Tr key={entry.externalId}>
                        <Table.Td>{entry.title}</Table.Td>
                        <Table.Td>{entry.artist}</Table.Td>
                        <Table.Td>{entry.isrc}</Table.Td>
                        <Table.Td>{formatDuration(entry.durationMs)}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Stack>
          )}
        </Stack>
      )}

      <Select
        label="Policy"
        description={IMPORT_LIST_POLICY_HELP}
        data={IMPORT_LIST_POLICIES.map((entry) => ({ value: entry.value, label: entry.label }))}
        value={policy}
        allowDeselect={false}
        onChange={(next) => setPolicy(next ?? 'AddOnly')}
      />

      <Select
        label="Quality profile"
        placeholder="The default profile"
        data={(profiles.data ?? []).map((profile) => ({ value: String(profile.id), label: profile.name }))}
        value={profileId}
        error={serverFields.qualityProfileId}
        allowDeselect={false}
        onChange={setProfileId}
      />

      <Select
        label="Library"
        placeholder="The default library"
        data={(libraries.data ?? []).map((library) => ({ value: String(library.id), label: library.name }))}
        value={libraryId}
        error={serverFields.libraryId}
        allowDeselect={false}
        onChange={setLibraryId}
      />

      <NumberInput
        label="Sync every (hours)"
        description="0 = only when I ask"
        min={0}
        max={720}
        value={interval}
        error={serverFields.syncIntervalHours}
        onChange={setInterval}
      />

      <Switch label="Enabled" checked={enabled} onChange={(event) => setEnabled(event.currentTarget.checked)} />

      <Checkbox
        label="Keep as a Plex playlist"
        description="A Plex playlist with the list's name, updated in place"
        checked={plexPlaylist}
        onChange={(event) => setPlexPlaylist(event.currentTarget.checked)}
      />

      <Checkbox
        label="Write an .m3u8"
        description="In the library's Playlists folder"
        checked={m3uExport}
        onChange={(event) => setM3uExport(event.currentTarget.checked)}
      />

      {serverFields.settings !== undefined && (
        <Text size="sm" c="red">
          {serverFields.settings}
        </Text>
      )}

      {general.map(([field, message]) => (
        <Alert key={field} color="red" icon={<CircleAlert size={16} />} title={field}>
          {message}
        </Alert>
      ))}

      {save.error !== null &&
        serverFields.settings === undefined &&
        serverFields.name === undefined &&
        serverFields.sourceText === undefined &&
        general.length === 0 && (
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

/** The list of import lists, and the dialogs that add, edit, sync and delete one. */
export function ImportListsSettingsPage() {
  const lists = useImportLists();
  const schema = useImportListSchema();
  const update = useUpdateImportList();
  const remove = useDeleteImportList();
  const sync = useSyncImportList();

  const [picking, setPicking] = useState(false);
  const [type, setType] = useState<string | null>(null);
  const [form, setForm] = useState<{ list: ImportListResource | null; type: string } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ImportListResource | null>(null);
  const [syncing, setSyncing] = useState<{ id: number; commandId: number; name: string } | null>(null);

  const rows = lists.data ?? [];
  const providers = schema.data ?? [];
  const types = providers.map((provider) => provider.displayName);
  const picked = providers.find((provider) => provider.type === type);

  /** The whole resource, so a toggle of one switch does not drop the settings or the file. */
  const enabledInput = (list: ImportListResource, enabled: boolean): ImportListInput & { id: number } => ({
    id: Number(list.id),
    type: list.type,
    name: list.name,
    settings: asSettings(list.settings),
    policy: list.policy,
    qualityProfileId: Number(list.qualityProfileId),
    libraryId: Number(list.libraryId),
    enabled,
    syncIntervalHours: Number(list.syncIntervalHours),
    plexPlaylist: list.plexPlaylist,
    m3uExport: list.m3uExport,
  });

  const refetch = lists.refetch;

  return (
    <Stack gap="lg">
      <Title order={3}>Import lists</Title>

      <Text size="sm" c="dimmed">
        Wondarr reads each enabled list on its interval and adds the songs it holds. A list never deletes a file;
        what it does when a song leaves the list is the list's policy.
      </Text>

      <Group justify="flex-end">
        <Button
          leftSection={<Plus size={16} />}
          onClick={() => {
            setType(null);
            setPicking(true);
          }}
        >
          Add list
        </Button>
      </Group>

      {lists.isPending && <LoadingState />}

      {lists.error !== null && <ErrorState message={lists.error.message} />}

      {!lists.isPending && lists.error === null && rows.length === 0 && <EmptyState message="No import lists yet" />}

      {rows.length > 0 && (
        <Card withBorder padding="md">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={200}>Type</Table.Th>
                <Table.Th w={150}>Songs</Table.Th>
                <Table.Th w={220}>Last sync</Table.Th>
                <Table.Th w={90}>Enabled</Table.Th>
                <Table.Th w={300} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((list) => {
                const failed = list.lastSyncMessage?.startsWith('Failed:') ?? false;

                return (
                  <Table.Tr key={list.id}>
                    <Table.Td>{list.name}</Table.Td>
                    <Table.Td>{providers.find((provider) => provider.type === list.type)?.displayName ?? list.type}</Table.Td>
                    <Table.Td>
                      <Stack gap={0}>
                        <Text size="xs">Added {list.counts.added}</Text>
                        <Text size="xs">Unresolved {list.counts.unresolved}</Text>
                        <Text size="xs">Pending {list.counts.pending}</Text>
                        <Text size="xs">Removed {list.counts.removed}</Text>
                      </Stack>
                    </Table.Td>
                    <Table.Td>
                      <Stack gap={0}>
                        <Text size="sm">{formatSynced(list.lastSyncedAt)}</Text>
                        {list.lastSyncMessage !== null && list.lastSyncMessage !== '' && (
                          <Text size="xs" c={failed ? 'red' : 'dimmed'}>
                            {list.lastSyncMessage}
                          </Text>
                        )}
                      </Stack>
                    </Table.Td>
                    <Table.Td>
                      <Switch
                        aria-label={`Enable ${list.name}`}
                        checked={list.enabled}
                        disabled={update.isPending}
                        onChange={(event) =>
                          update.mutate(enabledInput(list, event.currentTarget.checked), {
                            onError: (error) => notifications.show({ message: error.message, color: 'red' }),
                          })
                        }
                      />
                    </Table.Td>
                    <Table.Td>
                      <Group gap="xs" justify="flex-end">
                        <Button
                          variant="light"
                          size="xs"
                          loading={sync.isPending && sync.variables === Number(list.id)}
                          onClick={() =>
                            sync.mutate(Number(list.id), {
                              onSuccess: (accepted) =>
                                setSyncing({
                                  id: Number(list.id),
                                  commandId: Number(accepted.commandId),
                                  name: list.name,
                                }),
                            })
                          }
                        >
                          Sync now
                        </Button>
                        <Button
                          variant="light"
                          size="xs"
                          component={Link}
                          to={`/add/unresolved?importListId=${String(list.id)}`}
                        >
                          Items
                        </Button>
                        <Button
                          variant="light"
                          size="xs"
                          onClick={() => setForm({ list, type: list.type })}
                        >
                          Edit
                        </Button>
                        <Button variant="light" color="red" size="xs" onClick={() => setPendingDelete(list)}>
                          Delete
                        </Button>
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      {syncing !== null && (
        <SyncProgress
          key={syncing.commandId}
          commandId={syncing.commandId}
          name={syncing.name}
          onFinished={() => void refetch()}
        />
      )}

      <Modal opened={picking} onClose={() => setPicking(false)} title="Add an import list">
        <Stack gap="md">
          <Select label="Type" data={types} value={type} onChange={setType} allowDeselect={false} />

          {schema.error !== null && (
            <Text size="sm" c="red">
              {schema.error.message}
            </Text>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPicking(false)}>
              Cancel
            </Button>
            <Button
              disabled={picked === undefined}
              onClick={() => {
                if (picked !== undefined) {
                  setPicking(false);
                  setForm({ list: null, type: picked.type });
                }
              }}
            >
              Continue
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={form !== null}
        onClose={() => setForm(null)}
        title={form?.list == null ? 'Add an import list' : 'Edit import list'}
        size="lg"
      >
        {form !== null && (
          <ImportListForm
            key={form.list?.id ?? form.type}
            list={form.list}
            provider={
              providers.find((provider) => provider.type === form.type) ?? { type: form.type, displayName: form.type, fields: [] }
            }
            onClose={() => setForm(null)}
          />
        )}
      </Modal>

      <Modal opened={pendingDelete !== null} onClose={() => setPendingDelete(null)} title="Delete import list">
        <Stack gap="md">
          <Text size="sm">
            The songs {pendingDelete?.name ?? 'this list'} added stay in the library; only the list and its items go.
          </Text>

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
