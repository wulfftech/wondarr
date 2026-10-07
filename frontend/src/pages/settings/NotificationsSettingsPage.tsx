import { Alert, Badge, Button, Card, Checkbox, Group, Modal, Select, Stack, Switch, Table, Text, TextInput, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, Plus } from 'lucide-react';
import { useState } from 'react';
import {
  NOTIFICATION_EVENTS,
  useCreateNotification,
  useDeleteNotification,
  useNotificationSchema,
  useNotifications,
  useTestNotification,
  useUpdateNotification,
  type NotificationInput,
  type NotificationResource,
  type NotificationSchemaResource,
  type NotificationSettings,
} from '../../api/notifications';
import { ValidationError } from '../../api/profiles';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { initialValues, SettingsField, wireValue } from '../../components/SchemaFields';

/**
 * Settings → Notifications (ARCHITECTURE §5.7): the endpoints Wondarr tells when something happens,
 * the events each one wants, and a test button per endpoint. The settings form is rendered from the
 * server's schema, so a provider added there appears here without a change to this page. The fields
 * themselves are drawn by `components/SchemaFields`, which the import lists share.
 */

/** Reads a resource's settings node as the object the provider's fields name. */
function asSettings(value: unknown): NotificationSettings {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as NotificationSettings) : {};
}

/** The label an event name shows as; an event the UI does not know reads as its own name. */
function eventLabel(name: string): string {
  return NOTIFICATION_EVENTS.find((event) => event.value === name)?.label ?? name;
}

/** The add/edit dialog: the name, the events, and the settings the schema asks for. */
function NotificationForm({
  notification,
  schema,
  onClose,
}: {
  notification: NotificationResource | null;
  schema: NotificationSchemaResource;
  onClose: () => void;
}) {
  const create = useCreateNotification();
  const update = useUpdateNotification();
  const test = useTestNotification();
  const save = notification === null ? create : update;

  const stored = notification === null ? {} : asSettings(notification.settings);
  const initial = initialValues(schema.fields, stored);

  const [name, setName] = useState(notification?.name ?? '');
  const [enabled, setEnabled] = useState(notification?.enabled ?? true);
  const [events, setEvents] = useState<string[]>(notification?.events ?? []);
  const [values, setValues] = useState<Record<string, unknown>>(initial.values);
  const [tested, setTested] = useState<'sent' | 'failed' | null>(null);

  // Which secrets the server masked. It never changes while the modal is open: an emptied field is
  // the untouched one again, and it is the mask — not the empty string — that goes back.
  const masked = initial.masked;

  const basic = schema.fields.filter((field) => !field.advanced);
  const advanced = schema.fields.filter((field) => field.advanced);

  const serverFields = save.error instanceof ValidationError ? save.error.fields : {};
  const testFields = test.error instanceof ValidationError ? test.error.fields : {};
  const general = Object.entries(serverFields).filter(
    ([field]) => field !== 'name' && field !== 'settings' && !schema.fields.some((one) => one.name === field),
  );

  const setValue = (field: (typeof schema.fields)[number], next: unknown) =>
    setValues((current) => ({ ...current, [field.name]: next }));

  const input = (id: number | undefined): NotificationInput => {
    const settings: NotificationSettings = {};

    for (const field of schema.fields) {
      settings[field.name] = wireValue(field, values[field.name], masked.has(field.name));
    }

    return {
      name,
      implementation: schema.implementation,
      enabled,
      events,
      settings,
      ...(id === undefined ? {} : { id }),
    };
  };

  const toggleEvent = (value: string, checked: boolean) =>
    setEvents((current) => (checked ? [...current, value] : current.filter((event) => event !== value)));

  const runTest = () => {
    setTested(null);
    test.mutate(input(notification === null ? undefined : Number(notification.id)), {
      onSuccess: () => setTested('sent'),
      onError: () => setTested('failed'),
    });
  };

  const submit = () => {
    const done = () => {
      notifications.show({ message: notification === null ? 'Notification added' : 'Saved', color: 'green' });
      onClose();
    };

    if (notification === null) {
      create.mutate(input(undefined), { onSuccess: done });
    } else {
      update.mutate({ ...input(Number(notification.id)), id: Number(notification.id) }, { onSuccess: done });
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

      <Switch label="Enabled" checked={enabled} onChange={(event) => setEnabled(event.currentTarget.checked)} />

      <Stack gap={4}>
        <Text size="sm" fw={500}>
          Events
        </Text>
        {NOTIFICATION_EVENTS.map((event) => (
          <Checkbox
            key={event.value}
            label={event.label}
            checked={events.includes(event.value)}
            onChange={(change) => toggleEvent(event.value, change.currentTarget.checked)}
          />
        ))}
      </Stack>

      {basic.map((field) => (
        <SettingsField
          key={field.name}
          field={field}
          value={values[field.name]}
          masked={masked.has(field.name)}
          error={serverFields[field.name]}
          onChange={(next) => setValue(field, next)}
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
                error={serverFields[field.name]}
                onChange={(next) => setValue(field, next)}
              />
            ))}
          </Stack>
        </details>
      )}

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
        general.length === 0 && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {save.error.message}
          </Alert>
        )}

      {tested === 'sent' && (
        <Text size="sm" c="green">
          Test sent
        </Text>
      )}

      {tested === 'failed' && (
        <Text size="sm" c="red">
          {testFields.settings ?? test.error?.message ?? 'The test message could not be sent.'}
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

/** The list of notifications, and the dialogs that add, edit and delete one. */
export function NotificationsSettingsPage() {
  const list = useNotifications();
  const schema = useNotificationSchema();
  const update = useUpdateNotification();
  const remove = useDeleteNotification();

  const [picking, setPicking] = useState(false);
  const [implementation, setImplementation] = useState<string | null>(null);
  const [form, setForm] = useState<{ notification: NotificationResource | null; implementation: string } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<NotificationResource | null>(null);

  const rows = list.data ?? [];
  const implementations = (schema.data ?? []).map((entry) => entry.implementation);

  /** The whole resource, so a toggle of one switch does not drop the settings or the events. */
  const enabledInput = (notification: NotificationResource, enabled: boolean): NotificationInput & { id: number } => ({
    id: Number(notification.id),
    name: notification.name,
    implementation: notification.implementation,
    enabled,
    events: [...notification.events],
    settings: asSettings(notification.settings),
  });

  const picked = (schema.data ?? []).find((entry) => entry.implementation === implementation);

  return (
    <Stack gap="lg">
      <Title order={3}>Notifications</Title>

      <Text size="sm" c="dimmed">
        Wondarr sends a message to each enabled notification when one of its events happens. Secrets are stored
        server-side: they are never shown again here.
      </Text>

      <Group justify="flex-end">
        <Button
          leftSection={<Plus size={16} />}
          onClick={() => {
            setImplementation(null);
            setPicking(true);
          }}
        >
          Add notification
        </Button>
      </Group>

      {list.isPending && <LoadingState />}

      {list.error !== null && <ErrorState message={list.error.message} />}

      {!list.isPending && list.error === null && rows.length === 0 && <EmptyState message="No notifications yet" />}

      {rows.length > 0 && (
        <Card withBorder padding="md">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={140}>Type</Table.Th>
                <Table.Th>Events</Table.Th>
                <Table.Th w={110}>Enabled</Table.Th>
                <Table.Th w={170} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((notification) => (
                <Table.Tr key={notification.id}>
                  <Table.Td>{notification.name}</Table.Td>
                  <Table.Td>{notification.implementation}</Table.Td>
                  <Table.Td>
                    <Group gap={4}>
                      {notification.events.map((event) => (
                        <Badge key={event} variant="light" size="sm">
                          {eventLabel(event)}
                        </Badge>
                      ))}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Switch
                      aria-label={`Enable ${notification.name}`}
                      checked={notification.enabled}
                      disabled={update.isPending}
                      onChange={(event) =>
                        update.mutate(enabledInput(notification, event.currentTarget.checked), {
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
                        onClick={() => setForm({ notification, implementation: notification.implementation })}
                      >
                        Edit
                      </Button>
                      <Button variant="light" color="red" size="xs" onClick={() => setPendingDelete(notification)}>
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

      <Modal opened={picking} onClose={() => setPicking(false)} title="Add a notification">
        <Stack gap="md">
          <Select
            label="Type"
            data={implementations}
            value={implementation}
            onChange={setImplementation}
            allowDeselect={false}
          />

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
                  setForm({ notification: null, implementation: picked.implementation });
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
        title={form?.notification == null ? 'Add a notification' : 'Edit notification'}
        size="lg"
      >
        {form !== null && (
          <NotificationForm
            key={form.notification?.id ?? form.implementation}
            notification={form.notification}
            schema={
              (schema.data ?? []).find((entry) => entry.implementation === form.implementation) ?? {
                implementation: form.implementation,
                fields: [],
              }
            }
            onClose={() => setForm(null)}
          />
        )}
      </Modal>

      <Modal opened={pendingDelete !== null} onClose={() => setPendingDelete(null)} title="Delete notification">
        <Stack gap="md">
          <Text size="sm">Wondarr stops sending to {pendingDelete?.name ?? 'this notification'} straight away.</Text>

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
