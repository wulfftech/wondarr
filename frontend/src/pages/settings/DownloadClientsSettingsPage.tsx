import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Modal,
  NumberInput,
  Radio,
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
import {
  useCreateDownloadClient,
  useDeleteDownloadClient,
  useDownloadClientSchema,
  useDownloadClients,
  useTestDownloadClient,
  useUpdateDownloadClient,
  type DownloadClientInput,
  type DownloadClientResource,
  type DownloadClientSchemaResource,
} from '../../api/downloadClients';
import { ValidationError } from '../../api/profiles';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { initialValues, SettingsField, wireValue, type PairLabels } from '../../components/SchemaFields';
import { initCaps } from '../../components/text';

/**
 * Settings → Download clients (DECISIONS build session 8 #1, #8): the qBittorrent and SABnzbd the
 * indexers' grabs go to, with the remote path mappings that turn the client's paths into the ones
 * Wondarr sees. The settings form is rendered from the server's schema.
 */

/** What each client type is for, in one line of the add picker. */
const TYPE_DESCRIPTIONS: Record<string, string> = {
  qbittorrent: 'Torrents — only the wanted files of a torrent are downloaded',
  sabnzbd: 'Usenet — whole posts, unpacked, only the wanted tracks imported',
};

const TYPE_NAMES: Record<string, string> = {
  qbittorrent: 'qBittorrent',
  sabnzbd: 'SABnzbd',
};

/** The two columns of a remote path mapping. */
const MAPPING_LABELS: PairLabels = { key: 'Path in the client', value: 'Path Wondarr sees' };

function typeName(type: string): string {
  return TYPE_NAMES[type.toLowerCase()] ?? initCaps(type);
}

function protocolName(protocol: string | null | undefined): string {
  return protocol?.toLowerCase() === 'usenet' ? 'Usenet' : 'Torrent';
}

function asSettings(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

/** The add/edit dialog: name, enabled, priority and the settings the schema asks for. */
function DownloadClientForm({
  client,
  schema,
  onClose,
}: {
  client: DownloadClientResource | null;
  schema: DownloadClientSchemaResource;
  onClose: () => void;
}) {
  const create = useCreateDownloadClient();
  const update = useUpdateDownloadClient();
  const test = useTestDownloadClient();
  const save = client === null ? create : update;

  const initial = initialValues(schema.fields, client === null ? {} : asSettings(client.settings));
  const masked = initial.masked;

  const [name, setName] = useState(client?.name ?? typeName(schema.type));
  const [enabled, setEnabled] = useState(client?.enabled ?? true);
  const [priority, setPriority] = useState<number | string>(client === null ? 1 : Number(client.priority));
  const [values, setValues] = useState<Record<string, unknown>>(initial.values);
  const [tested, setTested] = useState<{ success: boolean; error: string | null } | null>(null);

  const fields = save.error instanceof ValidationError ? save.error.fields : {};

  const input = (id: number | undefined): DownloadClientInput => {
    const settings: Record<string, unknown> = {};

    for (const field of schema.fields) {
      settings[field.name] = wireValue(field, values[field.name], masked.has(field.name));
    }

    return {
      name,
      type: schema.type,
      enabled,
      priority: typeof priority === 'number' ? priority : Number(priority) || 1,
      settings,
      ...(id === undefined ? {} : { id }),
    };
  };

  const runTest = () => {
    setTested(null);
    test.mutate(input(client === null ? undefined : Number(client.id)), {
      onSuccess: (result) => setTested({ success: result.success, error: result.error ?? null }),
      onError: (error) => setTested({ success: false, error: error.message }),
    });
  };

  const submit = () => {
    const done = () => {
      notifications.show({ message: client === null ? 'Download client added' : 'Saved', color: 'green' });
      onClose();
    };

    if (client === null) {
      create.mutate(input(undefined), { onSuccess: done });
    } else {
      update.mutate({ ...input(Number(client.id)), id: Number(client.id) }, { onSuccess: done });
    }
  };

  const field = (entry: DownloadClientSchemaResource['fields'][number]) => (
    <SettingsField
      key={entry.name}
      field={entry}
      value={values[entry.name]}
      masked={masked.has(entry.name)}
      error={fields[entry.name]}
      pairLabels={entry.name === 'remotePathMappings' ? MAPPING_LABELS : undefined}
      onChange={(next) => setValues((current) => ({ ...current, [entry.name]: next }))}
    />
  );

  const advanced = schema.fields.filter((entry) => entry.advanced);

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
        description="When several clients serve a protocol, the lowest number is the default"
        min={1}
        max={50}
        value={priority}
        error={fields.priority}
        onChange={setPriority}
      />

      {schema.fields.filter((entry) => !entry.advanced).map(field)}

      {advanced.length > 0 && (
        <details>
          <summary>Advanced</summary>
          <Stack gap="md" mt="sm">
            {advanced.map(field)}
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

/** The list of download clients, and the dialogs that add, edit and delete one. */
export function DownloadClientsSettingsPage() {
  const list = useDownloadClients();
  const schema = useDownloadClientSchema();
  const update = useUpdateDownloadClient();
  const remove = useDeleteDownloadClient();

  const [picking, setPicking] = useState(false);
  const [type, setType] = useState<string | null>(null);
  const [form, setForm] = useState<{ client: DownloadClientResource | null; type: string } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<DownloadClientResource | null>(null);

  const rows = list.data ?? [];
  const types = schema.data ?? [];
  const picked = types.find((entry) => entry.type === type);

  const enabledInput = (client: DownloadClientResource, enabled: boolean): DownloadClientInput & { id: number } => ({
    id: Number(client.id),
    name: client.name,
    type: client.type,
    enabled,
    priority: Number(client.priority),
    settings: asSettings(client.settings),
  });

  return (
    <Stack gap="lg">
      <Title order={3}>Download clients</Title>

      <Text size="sm" c="dimmed">
        The indexers&apos; grabs go to these clients. Wondarr never moves or deletes a torrent&apos;s data: the wanted
        file is linked into Wondarr&apos;s staging folder and the torrent keeps seeding.
      </Text>

      <Group justify="flex-end">
        <Button
          leftSection={<Plus size={16} />}
          onClick={() => {
            setType(null);
            setPicking(true);
          }}
        >
          Add download client
        </Button>
      </Group>

      {list.isPending && <LoadingState />}

      {list.error !== null && <ErrorState message={list.error.message} />}

      {!list.isPending && list.error === null && rows.length === 0 && <EmptyState message="No download clients yet" />}

      {rows.length > 0 && (
        <Card withBorder padding="md">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={130}>Type</Table.Th>
                <Table.Th w={100}>Protocol</Table.Th>
                <Table.Th w={80}>Priority</Table.Th>
                <Table.Th w={90}>Enabled</Table.Th>
                <Table.Th w={170} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((client) => (
                <Table.Tr key={client.id}>
                  <Table.Td>{client.name}</Table.Td>
                  <Table.Td>{typeName(client.type)}</Table.Td>
                  <Table.Td>
                    <Badge variant="light" color={protocolName(client.protocol) === 'Usenet' ? 'grape' : 'teal'}>
                      {protocolName(client.protocol)}
                    </Badge>
                  </Table.Td>
                  <Table.Td>{client.priority}</Table.Td>
                  <Table.Td>
                    <Switch
                      aria-label={`Enable ${client.name}`}
                      checked={client.enabled}
                      disabled={update.isPending}
                      onChange={(event) =>
                        update.mutate(enabledInput(client, event.currentTarget.checked), {
                          onError: (error) => notifications.show({ message: error.message, color: 'red' }),
                        })
                      }
                    />
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs" justify="flex-end">
                      <Button variant="light" size="xs" onClick={() => setForm({ client, type: client.type })}>
                        Edit
                      </Button>
                      <Button variant="light" color="red" size="xs" onClick={() => setPendingDelete(client)}>
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

      <Modal opened={picking} onClose={() => setPicking(false)} title="Add a download client">
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
                  setForm({ client: null, type: picked.type });
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
        title={form?.client == null ? 'Add a download client' : 'Edit download client'}
        size="lg"
      >
        {form !== null && (
          <DownloadClientForm
            key={form.client?.id ?? form.type}
            client={form.client}
            schema={
              types.find((entry) => entry.type === form.type) ?? { type: form.type, protocol: 'torrent', fields: [] }
            }
            onClose={() => setForm(null)}
          />
        )}
      </Modal>

      <Modal opened={pendingDelete !== null} onClose={() => setPendingDelete(null)} title="Delete download client">
        <Stack gap="md">
          <Text size="sm">
            Wondarr stops sending grabs to {pendingDelete?.name ?? 'this client'}. Downloads already in it are left
            alone.
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
