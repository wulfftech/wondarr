import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Modal,
  NumberInput,
  Radio,
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
import { useState } from 'react';
import { useDownloadClients, type DownloadClientResource } from '../../api/downloadClients';
import {
  useCreateIndexer,
  useDeleteIndexer,
  useIndexerSchema,
  useIndexers,
  useTestIndexer,
  useUpdateIndexer,
  type DownloadProtocol,
  type IndexerInput,
  type IndexerResource,
  type IndexerSchemaResource,
} from '../../api/indexers';
import { ValidationError } from '../../api/profiles';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { initialValues, SettingsField, wireValue } from '../../components/SchemaFields';
import { initCaps } from '../../components/text';

/**
 * Settings → Indexers (DECISIONS build session 8 #1): the Torznab and Newznab feeds (and Prowlarr,
 * and Gazelle trackers) Wondarr searches for releases that hold a wanted song. The settings form is
 * rendered from the server's schema, exactly as notifications are.
 */

/** What each indexer type is for, in one line of the add picker. */
const TYPE_DESCRIPTIONS: Record<string, string> = {
  torznab: "A Torznab feed — Prowlarr's per-indexer URL, Jackett",
  newznab: "A Newznab feed — Prowlarr's per-indexer URL, NZBHydra, a usenet indexer",
  prowlarr: 'Search every indexer in Prowlarr at once',
  gazelle: 'A Gazelle tracker (Redacted, Orpheus) with your API key',
};

/** How a type name reads; the server's own name, capitalised, when it is not one of these. */
const TYPE_NAMES: Record<string, string> = {
  torznab: 'Torznab',
  newznab: 'Newznab',
  prowlarr: 'Prowlarr',
  gazelle: 'Gazelle',
};

/** The label a type shows as. */
function typeName(type: string): string {
  return TYPE_NAMES[type.toLowerCase()] ?? initCaps(type);
}

/** The label a protocol shows as. */
function protocolName(protocol: string | null | undefined): string {
  return protocol?.toLowerCase() === 'usenet' ? 'Usenet' : 'Torrent';
}

function asSettings(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function asProtocol(value: string | null | undefined): DownloadProtocol {
  return value?.toLowerCase() === 'usenet' ? 'usenet' : 'torrent';
}

/** The add/edit dialog: name, enabled, priority, protocol (when the type lets the row choose), client and settings. */
function IndexerForm({
  indexer,
  schema,
  clients,
  onClose,
}: {
  indexer: IndexerResource | null;
  schema: IndexerSchemaResource;
  clients: DownloadClientResource[];
  onClose: () => void;
}) {
  const create = useCreateIndexer();
  const update = useUpdateIndexer();
  const test = useTestIndexer();
  const save = indexer === null ? create : update;

  const initial = initialValues(schema.fields, indexer === null ? {} : asSettings(indexer.settings));
  const masked = initial.masked;

  const [name, setName] = useState(indexer?.name ?? typeName(schema.type));
  const [enabled, setEnabled] = useState(indexer?.enabled ?? true);
  const [priority, setPriority] = useState<number | string>(indexer === null ? 25 : Number(indexer.priority));
  const [protocol, setProtocol] = useState<DownloadProtocol>(asProtocol(indexer?.protocol ?? schema.protocol));
  const [clientId, setClientId] = useState<string>(
    indexer?.downloadClientId == null ? 'default' : String(indexer.downloadClientId),
  );
  const [values, setValues] = useState<Record<string, unknown>>(initial.values);
  const [tested, setTested] = useState<{ success: boolean; error: string | null } | null>(null);

  const fields = save.error instanceof ValidationError ? save.error.fields : {};
  const ofProtocol = clients.filter((client) => asProtocol(client.protocol) === protocol);
  const clientOptions = [
    { value: 'default', label: `Default ${protocolName(protocol).toLowerCase()} client` },
    ...ofProtocol.map((client) => ({ value: String(client.id), label: client.name })),
  ];

  const input = (id: number | undefined): IndexerInput => {
    const settings: Record<string, unknown> = {};

    for (const field of schema.fields) {
      settings[field.name] = wireValue(field, values[field.name], masked.has(field.name));
    }

    return {
      name,
      type: schema.type,
      protocol: schema.protocolChoosable ? protocol : null,
      enabled,
      priority: typeof priority === 'number' ? priority : Number(priority) || 25,
      downloadClientId: clientId === 'default' ? null : Number(clientId),
      settings,
      ...(id === undefined ? {} : { id }),
    };
  };

  const runTest = () => {
    setTested(null);
    test.mutate(input(indexer === null ? undefined : Number(indexer.id)), {
      onSuccess: (result) => setTested({ success: result.success, error: result.error ?? null }),
      onError: (error) => setTested({ success: false, error: error.message }),
    });
  };

  const submit = () => {
    const done = () => {
      notifications.show({ message: indexer === null ? 'Indexer added' : 'Saved', color: 'green' });
      onClose();
    };

    if (indexer === null) {
      create.mutate(input(undefined), { onSuccess: done });
    } else {
      update.mutate({ ...input(Number(indexer.id)), id: Number(indexer.id) }, { onSuccess: done });
    }
  };

  const basic = schema.fields.filter((field) => !field.advanced);
  const advanced = schema.fields.filter((field) => field.advanced);

  return (
    <Stack gap="md">
      <TextInput
        label="Name"
        value={name}
        error={fields.name}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      <Switch label="Enabled" checked={enabled} onChange={(event) => setEnabled(event.currentTarget.checked)} />

      <NumberInput
        label="Priority"
        description="1–50; a lower number is asked first"
        min={1}
        max={50}
        value={priority}
        error={fields.priority}
        onChange={setPriority}
      />

      {schema.protocolChoosable && (
        <Radio.Group
          label="Protocol"
          value={protocol}
          onChange={(value) => {
            setProtocol(asProtocol(value));
            setClientId('default');
          }}
        >
          <Group mt={4}>
            <Radio value="torrent" label="Torrent" />
            <Radio value="usenet" label="Usenet" />
          </Group>
        </Radio.Group>
      )}

      <Select
        label="Download client"
        data={clientOptions}
        value={clientId}
        error={fields.downloadClientId}
        allowDeselect={false}
        onChange={(value) => setClientId(value ?? 'default')}
      />

      {basic.map((field) => (
        <SettingsField
          key={field.name}
          field={field}
          value={values[field.name]}
          masked={masked.has(field.name)}
          error={fields[field.name]}
          onChange={(next) => setValues((current) => ({ ...current, [field.name]: next }))}
        />
      ))}

      {advanced.length > 0 && (
        <details>
          <summary>Advanced</summary>
          <Stack gap="md" mt="sm">
            {advanced.map((field) => (
              <SettingsField
                key={field.name}
                field={field}
                value={values[field.name]}
                masked={masked.has(field.name)}
                error={fields[field.name]}
                onChange={(next) => setValues((current) => ({ ...current, [field.name]: next }))}
              />
            ))}
          </Stack>
        </details>
      )}

      {save.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {save.error.message}
        </Alert>
      )}

      {tested !== null && (
        <Text size="sm" c={tested.success ? 'green' : 'red'}>
          {tested.success ? 'Connected' : (tested.error ?? 'The test failed.')}
        </Text>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button variant="light" loading={test.isPending} onClick={runTest}>
          Test
        </Button>
        <Button loading={save.isPending} onClick={submit}>
          Save
        </Button>
      </Group>
    </Stack>
  );
}

/** The list of indexers, and the dialogs that add, edit and delete one. */
export function IndexersSettingsPage() {
  const list = useIndexers();
  const schema = useIndexerSchema();
  const clients = useDownloadClients();
  const update = useUpdateIndexer();
  const remove = useDeleteIndexer();

  const [picking, setPicking] = useState(false);
  const [type, setType] = useState<string | null>(null);
  const [form, setForm] = useState<{ indexer: IndexerResource | null; type: string } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<IndexerResource | null>(null);

  const rows = list.data ?? [];
  const types = schema.data ?? [];
  const clientRows = clients.data ?? [];
  const picked = types.find((entry) => entry.type === type);

  const clientName = (indexer: IndexerResource) => {
    const id = indexer.downloadClientId;

    return id == null
      ? `Default ${protocolName(indexer.protocol).toLowerCase()} client`
      : (clientRows.find((client) => Number(client.id) === Number(id))?.name ?? `Client ${String(id)}`);
  };

  const enabledInput = (indexer: IndexerResource, enabled: boolean): IndexerInput & { id: number } => ({
    id: Number(indexer.id),
    name: indexer.name,
    type: indexer.type,
    protocol: asProtocol(indexer.protocol),
    enabled,
    priority: Number(indexer.priority),
    downloadClientId: indexer.downloadClientId == null ? null : Number(indexer.downloadClientId),
    settings: asSettings(indexer.settings),
  });

  return (
    <Stack gap="lg">
      <Title order={3}>Indexers</Title>

      <Text size="sm" c="dimmed">
        Wondarr searches the enabled indexers for releases that hold a wanted song, after Soulseek and YouTube, and
        downloads only that song&apos;s file. Secrets are stored server-side: they are never shown again here.
      </Text>

      <Group justify="flex-end">
        <Button
          leftSection={<Plus size={16} />}
          onClick={() => {
            setType(null);
            setPicking(true);
          }}
        >
          Add indexer
        </Button>
      </Group>

      {list.isPending && <LoadingState />}

      {list.error !== null && <ErrorState message={list.error.message} />}

      {!list.isPending && list.error === null && rows.length === 0 && <EmptyState message="No indexers yet" />}

      {rows.length > 0 && (
        <Card withBorder padding="md">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={110}>Type</Table.Th>
                <Table.Th w={100}>Protocol</Table.Th>
                <Table.Th w={80}>Priority</Table.Th>
                <Table.Th>Client</Table.Th>
                <Table.Th w={90}>Enabled</Table.Th>
                <Table.Th w={170} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((indexer) => (
                <Table.Tr key={indexer.id}>
                  <Table.Td>{indexer.name}</Table.Td>
                  <Table.Td>{typeName(indexer.type)}</Table.Td>
                  <Table.Td>
                    <Badge variant="light" color={asProtocol(indexer.protocol) === 'usenet' ? 'grape' : 'teal'}>
                      {protocolName(indexer.protocol)}
                    </Badge>
                  </Table.Td>
                  <Table.Td>{indexer.priority}</Table.Td>
                  <Table.Td>{clientName(indexer)}</Table.Td>
                  <Table.Td>
                    <Switch
                      aria-label={`Enable ${indexer.name}`}
                      checked={indexer.enabled}
                      disabled={update.isPending}
                      onChange={(event) =>
                        update.mutate(enabledInput(indexer, event.currentTarget.checked), {
                          onError: (error) => notifications.show({ message: error.message, color: 'red' }),
                        })
                      }
                    />
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs" justify="flex-end">
                      <Button variant="light" size="xs" onClick={() => setForm({ indexer, type: indexer.type })}>
                        Edit
                      </Button>
                      <Button variant="light" color="red" size="xs" onClick={() => setPendingDelete(indexer)}>
                        Delete
                      </Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      <Modal opened={picking} onClose={() => setPicking(false)} title="Add an indexer">
        <Stack gap="md">
          <Radio.Group label="Type" value={type ?? ''} onChange={setType}>
            <Stack gap="xs" mt={4}>
              {types.map((entry) => (
                <Radio
                  key={entry.type}
                  value={entry.type}
                  label={typeName(entry.type)}
                  description={TYPE_DESCRIPTIONS[entry.type.toLowerCase()]}
                />
              ))}
            </Stack>
          </Radio.Group>

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
                  setForm({ indexer: null, type: picked.type });
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
        title={form?.indexer == null ? 'Add an indexer' : 'Edit indexer'}
        size="lg"
      >
        {form !== null && (
          <IndexerForm
            key={form.indexer?.id ?? form.type}
            indexer={form.indexer}
            schema={
              types.find((entry) => entry.type === form.type) ?? {
                type: form.type,
                protocol: null,
                protocolChoosable: false,
                fields: [],
              }
            }
            clients={clientRows}
            onClose={() => setForm(null)}
          />
        )}
      </Modal>

      <Modal opened={pendingDelete !== null} onClose={() => setPendingDelete(null)} title="Delete indexer">
        <Stack gap="md">
          <Text size="sm">Wondarr stops searching {pendingDelete?.name ?? 'this indexer'} straight away.</Text>

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
