import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  Modal,
  PasswordInput,
  Radio,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, CircleCheck } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import {
  useCreatePlexPin,
  usePlexPinStatus,
  usePlexServers,
  usePlexState,
  useSelectPlexServer,
  useSetPlexToken,
  useSignOutPlex,
  useTestPlex,
  type PlexConnectionResource,
  type PlexPinResource,
  type PlexServerResource,
  type PlexStateResource,
  type PlexTestResource,
} from '../../api/plex';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

/** A connection's URIs read better when the relay — the slowest way in — is offered last. */
function byRelayLast(a: PlexConnectionResource, b: PlexConnectionResource): number {
  return Number(a.relay) - Number(b.relay);
}

/** One connection of a server: its URI, and how plex.tv would reach it. */
function ConnectionLabel({ connection }: { connection: PlexConnectionResource }) {
  return (
    <Group gap="xs" wrap="nowrap">
      <Text size="sm" ff="monospace">
        {connection.uri}
      </Text>
      <Badge size="xs" variant="light" color={connection.local ? 'green' : 'gray'}>
        {connection.local ? 'local' : 'remote'}
      </Badge>
      {connection.relay && (
        <Badge size="xs" variant="light" color="orange">
          relay
        </Badge>
      )}
    </Group>
  );
}

/** What a successful connection test reads as. */
function testText(result: PlexTestResource): string {
  const name = result.serverName ?? 'the server';
  const version = result.version === null ? 'version unknown' : `Plex ${result.version}`;

  return `Connected to ${name} (${version}), ${String(result.musicSections)} music libraries`;
}

/**
 * The collapsed fallback for an install that cannot open a browser tab: the token the user copied
 * from Plex's own UI. The field is emptied and the mutation reset as soon as the server accepts it,
 * so the token is left neither in component state nor in the mutation cache.
 */
function PasteTokenForm() {
  const save = useSetPlexToken();
  const [open, setOpen] = useState(false);
  const [token, setToken] = useState('');

  const submit = () => {
    // Success is announced by the page, which sees the state turn signed in either way. The
    // mutation keeps its variables until it is reset, so it is reset too: the token must not stay
    // readable from React Query's mutation cache.
    save.mutate(token, {
      onSuccess: () => {
        setToken('');
        save.reset();
      },
    });
  };

  return (
    <Stack gap="xs">
      <Anchor component="button" type="button" size="sm" onClick={() => setOpen((shown) => !shown)}>
        Paste a token instead
      </Anchor>

      <Collapse expanded={open}>
        <Stack gap="xs">
          <PasswordInput label="Plex token" value={token} onChange={(event) => setToken(event.currentTarget.value)} />

          {save.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {save.error.message}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="light" loading={save.isPending} disabled={token === ''} onClick={submit}>
              Save token
            </Button>
          </Group>
        </Stack>
      </Collapse>
    </Stack>
  );
}

/**
 * The wait between creating a PIN and plex.tv approving it: the code, the polls, and the two ways
 * the wait ends. A retry clears the spent PIN, which puts the sign-in button back.
 */
function PinWait({ pin, onRetry }: { pin: PlexPinResource; onRetry: () => void }) {
  const status = usePlexPinStatus(pin.id, true);

  if (status.data?.expired === true) {
    return (
      <Alert color="red" icon={<CircleAlert size={16} />} title="The sign-in code expired">
        <Stack gap="xs" align="flex-start">
          <Text size="sm">plex.tv stopped accepting that code before it was approved.</Text>
          <Button variant="light" size="xs" onClick={onRetry}>
            Get a new code
          </Button>
        </Stack>
      </Alert>
    );
  }

  return (
    <Stack gap="xs">
      <Text size="sm">Enter this code on plex.tv to approve Wondarr:</Text>
      <Text size="xl" fw={700} ff="monospace">
        {pin.code}
      </Text>
      <Text size="sm" c="dimmed">
        Waiting for you to approve Wondarr on plex.tv…
      </Text>

      {status.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {status.error.message}
        </Alert>
      )}
    </Stack>
  );
}

/** The sign-in card: the PIN flow, and the pasted token as its fallback. */
function SignInCard() {
  const createPin = useCreatePlexPin();
  const [pin, setPin] = useState<PlexPinResource | null>(null);

  const start = () => {
    createPin.mutate(undefined, {
      onSuccess: (created) => {
        setPin(created);
        // A new tab leaves the user on this page, where the poll is already waiting for them.
        window.open(created.authUrl, '_blank', 'noopener');
      },
    });
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="sm">
        <Text fw={600}>Sign in</Text>

        {pin === null ? (
          <>
            <Text size="sm">Approve this install on plex.tv to let Wondarr reach your server.</Text>

            <Group>
              <Button loading={createPin.isPending} onClick={start}>
                Sign in with Plex
              </Button>
            </Group>
          </>
        ) : (
          <PinWait pin={pin} onRetry={() => setPin(null)} />
        )}

        {createPin.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {createPin.error.message}
          </Alert>
        )}

        <PasteTokenForm />
      </Stack>
    </Card>
  );
}

/** The list of servers the account can reach, each with every connection it offers. */
function ServerPicker({ selected, onSelect }: { selected: string | null; onSelect: (uri: string) => void }) {
  const servers = usePlexServers(true);

  if (servers.isPending) {
    return <LoadingState label="Loading Plex servers…" />;
  }

  if (servers.error !== null) {
    return <ErrorState message={servers.error.message} />;
  }

  const rows = servers.data ?? [];

  if (rows.length === 0) {
    return <EmptyState message="The signed-in Plex account can see no servers." />;
  }

  return (
    <Stack gap="md">
      {rows.map((server: PlexServerResource) => (
        <Stack key={server.machineIdentifier} gap="xs">
          <Group gap="xs">
            <Text size="sm" fw={500}>
              {server.name}
            </Text>
            <Badge size="xs" variant="light">
              {server.owned ? 'owned' : 'shared'}
            </Badge>
            {server.productVersion !== null && (
              <Text size="xs" c="dimmed">
                {server.productVersion}
              </Text>
            )}
          </Group>

          <Radio.Group value={selected} onChange={onSelect}>
            <Stack gap={4}>
              {[...server.connections].sort(byRelayLast).map((connection) => (
                <Radio
                  key={connection.uri}
                  value={connection.uri}
                  label={<ConnectionLabel connection={connection} />}
                />
              ))}
            </Stack>
          </Radio.Group>
        </Stack>
      ))}
    </Stack>
  );
}

/** The signed-in view: the chosen server, how to change it, a test, and signing out. */
function ServerCard({ state }: { state: PlexStateResource }) {
  const select = useSelectPlexServer();
  const test = useTestPlex();
  const signOut = useSignOutPlex();
  const [selected, setSelected] = useState<string | null>(state.serverUrl);
  const [customUrl, setCustomUrl] = useState('');
  const [confirming, setConfirming] = useState(false);

  // A URL typed by hand wins over the picked connection; using one clears the other.
  const url = customUrl.trim() === '' ? selected : customUrl.trim();

  const useServer = () => {
    if (url === null) {
      return;
    }

    select.mutate(url, {
      onSuccess: (next) =>
        notifications.show({
          message: next.serverName === null ? 'Server selected' : `Server selected: ${next.serverName}`,
          color: 'green',
        }),
    });
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Text fw={600}>Server</Text>

        {state.serverUrl === null ? (
          <Text size="sm" c="dimmed">
            No server selected
          </Text>
        ) : (
          <Text size="sm">
            {state.serverName ?? 'Plex server'} · {state.serverUrl}
          </Text>
        )}

        <ServerPicker
          selected={selected}
          onSelect={(uri) => {
            setSelected(uri);
            setCustomUrl('');
          }}
        />

        <TextInput
          label="Server URL"
          description="For a server behind a reverse proxy, or one plex.tv does not list."
          placeholder="http://plex.lan:32400"
          value={customUrl}
          onChange={(event) => {
            setCustomUrl(event.currentTarget.value);
            setSelected(null);
          }}
        />

        {select.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {select.error.message}
          </Alert>
        )}

        <Group>
          <Button loading={select.isPending} disabled={url === null} onClick={useServer}>
            Use this server
          </Button>
          <Button variant="light" loading={test.isPending} onClick={() => test.mutate()}>
            Test
          </Button>
        </Group>

        {test.data !== undefined &&
          (test.data.ok ? (
            <Alert color="green" icon={<CircleCheck size={16} />}>
              {testText(test.data)}
            </Alert>
          ) : (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {test.data.error ?? 'The server did not answer.'}
            </Alert>
          ))}

        {test.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {test.error.message}
          </Alert>
        )}

        <Text size="xs" c="dimmed">
          This install&apos;s Plex client id: {state.clientIdentifier}
        </Text>

        <Group>
          <Button variant="default" onClick={() => setConfirming(true)}>
            Sign out
          </Button>
        </Group>

        <Modal opened={confirming} onClose={() => setConfirming(false)} title="Sign out of Plex?">
          <Stack gap="md">
            <Text size="sm">
              Wondarr forgets the Plex sign-in and stops refreshing the linked libraries. Nothing already placed in the
              library is touched.
            </Text>

            {signOut.error !== null && (
              <Alert color="red" icon={<CircleAlert size={16} />}>
                {signOut.error.message}
              </Alert>
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirming(false)}>
                Cancel
              </Button>
              <Button
                color="red"
                loading={signOut.isPending}
                onClick={() =>
                  signOut.mutate(undefined, {
                    onSuccess: () => {
                      setConfirming(false);
                      notifications.show({ message: 'Signed out of Plex', color: 'green' });
                    },
                  })
                }
              >
                Sign out of Plex
              </Button>
            </Group>
          </Stack>
        </Modal>
      </Stack>
    </Card>
  );
}

/** Settings → Plex: the sign-in, the server Wondarr talks to, and what that server is doing. */
export function PlexSettingsPage() {
  const state = usePlexState();
  const signedIn = state.data?.signedIn;
  const previous = useRef<boolean | null>(null);

  // The moment a sign-in is worth announcing: the state said "signed out" and now says otherwise,
  // whichever way the token arrived — the PIN the user approved, or one they pasted.
  useEffect(() => {
    if (signedIn === undefined) {
      return;
    }

    if (previous.current === false && signedIn) {
      notifications.show({ message: 'Signed in to Plex', color: 'green' });
    }

    previous.current = signedIn;
  }, [signedIn]);

  return (
    <Stack gap="lg">
      <Title order={3}>Plex</Title>

      {state.isPending && <LoadingState label="Loading the Plex connection…" />}

      {state.error !== null && <ErrorState message={state.error.message} />}

      {state.data !== undefined &&
        (state.data.signedIn ? <ServerCard key={state.data.serverUrl ?? ''} state={state.data} /> : <SignInCard />)}
    </Stack>
  );
}
