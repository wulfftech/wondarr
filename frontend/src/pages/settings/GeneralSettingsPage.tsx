import {
  Alert,
  Button,
  Card,
  Code,
  CopyButton,
  Group,
  PasswordInput,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState, type SyntheticEvent } from 'react';
import { AuthUserUpdateError, useAuthUser, useSetAuthUser } from '../../api/auth';
import { useAppConfig } from '../../api/context';
import { useSystemStatus } from '../../api/hooks';
import { ErrorState, LoadingState } from '../../components/DataState';

const MASK = '••••••••';
const MAX_USERNAME_LENGTH = 64;
const MIN_PASSWORD_LENGTH = 8;

/** The API key as the card shows it: eight bullets and the last four characters, until "Show". */
function maskedKey(apiKey: string): string {
  return apiKey.length > 4 ? `${MASK}${apiKey.slice(-4)}` : MASK;
}

function ApiKeyBlock() {
  const { apiKey } = useAppConfig();
  const [shown, setShown] = useState(false);

  return (
    <Stack gap="xs">
      <Text fw={500}>API key</Text>
      <Group gap="xs" wrap="nowrap">
        <Code data-testid="api-key" style={{ flex: 1, overflowWrap: 'anywhere' }}>
          {shown ? apiKey : maskedKey(apiKey)}
        </Code>
        <Button
          variant="default"
          size="xs"
          onClick={() => {
            setShown((value) => !value);
          }}
        >
          {shown ? 'Hide' : 'Show'}
        </Button>
        <CopyButton value={apiKey}>
          {({ copied, copy }) => (
            <Button variant="light" size="xs" onClick={copy}>
              {copied ? 'Copied' : 'Copy'}
            </Button>
          )}
        </CopyButton>
      </Group>
      <Text size="sm" c="dimmed">
        Give this key to Prowlarr, autobrr, Homepage or Unpackerr. It is set in <Code>/config/config.yml</Code> (
        <Code>server.api_key</Code>) or <Code>APP__SERVER__API_KEY</Code>.
      </Text>
    </Stack>
  );
}

function LoginBlock() {
  const user = useAuthUser();
  const save = useSetAuthUser();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [localErrors, setLocalErrors] = useState<{ username?: string; password?: string; confirm?: string }>({});
  const [saved, setSaved] = useState(false);

  const serverErrors = save.error instanceof AuthUserUpdateError ? save.error.fieldErrors : {};
  const serverGeneral =
    save.error !== null && !(save.error instanceof AuthUserUpdateError && Object.keys(serverErrors).length > 0)
      ? save.error.message
      : null;

  function submit(event: SyntheticEvent<HTMLFormElement>): void {
    event.preventDefault();
    setSaved(false);

    const errors: typeof localErrors = {};
    const name = username.trim();

    if (name.length < 1 || name.length > MAX_USERNAME_LENGTH) {
      errors.username = `Username must be between 1 and ${MAX_USERNAME_LENGTH} characters.`;
    }

    if (password.length < MIN_PASSWORD_LENGTH) {
      errors.password = `Password must be at least ${MIN_PASSWORD_LENGTH} characters.`;
    }

    if (password !== confirm) {
      errors.confirm = 'The passwords do not match.';
    }

    setLocalErrors(errors);

    if (Object.keys(errors).length > 0) {
      return;
    }

    save.mutate(
      { username: name, password },
      {
        onSuccess: () => {
          setUsername('');
          setPassword('');
          setConfirm('');
          setSaved(true);
        },
      },
    );
  }

  return (
    <Stack gap="xs">
      <Text fw={500}>Login</Text>
      {user.isPending ? (
        <LoadingState label="Loading the login account…" />
      ) : user.isError ? (
        <ErrorState title="The login account could not be loaded" message={user.error.message} />
      ) : (
        <Text size="sm">
          {user.data.configured && user.data.username !== null ? (
            <>
              Signed in as <b>{user.data.username}</b>
            </>
          ) : (
            'No login account yet'
          )}
        </Text>
      )}
      <form onSubmit={submit}>
        <Stack gap="sm">
          <TextInput
            label="Username"
            value={username}
            maxLength={MAX_USERNAME_LENGTH}
            error={localErrors.username ?? serverErrors.username?.join(' ')}
            onChange={(event) => {
              setUsername(event.currentTarget.value);
            }}
          />
          <PasswordInput
            label="Password"
            value={password}
            error={localErrors.password ?? serverErrors.password?.join(' ')}
            onChange={(event) => {
              setPassword(event.currentTarget.value);
            }}
          />
          <PasswordInput
            label="Confirm password"
            value={confirm}
            error={localErrors.confirm}
            onChange={(event) => {
              setConfirm(event.currentTarget.value);
            }}
          />
          {serverGeneral !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {serverGeneral}
            </Alert>
          )}
          {saved && (
            <Alert color="green" role="status">
              Login saved.
            </Alert>
          )}
          <Group>
            <Button type="submit" loading={save.isPending}>
              Save
            </Button>
          </Group>
        </Stack>
      </form>
      <Text size="sm" c="dimmed">
        Wondarr asks for this login when it is reached from outside your network (
        <Code>server.auth_required: disabledForLocalAddresses</Code>, the default).
      </Text>
    </Stack>
  );
}

function InstanceCard() {
  const config = useAppConfig();
  const status = useSystemStatus();

  return (
    <Card withBorder padding="md">
      <Stack gap="xs">
        <Title order={4}>Instance</Title>
        <Text size="sm">Version: {config.version}</Text>
        <Text size="sm">URL base: {config.urlBase === '' ? '(site root)' : config.urlBase}</Text>
        {status.data !== undefined && <Text size="sm">Authentication: {String(status.data.authentication)}</Text>}
        <Text size="sm" c="dimmed">
          Change these in <Code>/config/config.yml</Code> and restart.
        </Text>
      </Stack>
    </Card>
  );
}

/** Settings → General: the API key, the login account, and how this instance is reached. */
export function GeneralSettingsPage() {
  return (
    <Stack gap="md">
      <Card withBorder padding="md">
        <Stack gap="lg">
          <Title order={4}>Security</Title>
          <ApiKeyBlock />
          <LoginBlock />
        </Stack>
      </Card>
      <InstanceCard />
    </Stack>
  );
}
