import {
  Alert,
  Anchor,
  Button,
  Card,
  Code,
  CopyButton,
  Group,
  PasswordInput,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState, type SyntheticEvent } from 'react';
import { AuthUserUpdateError, useAuthUser, useSetAuthUser } from '../../api/auth';
import { useAppConfig } from '../../api/context';
import { useSystemStatus } from '../../api/hooks';
import {
  MetadataUpdateError,
  useMetadataSettings,
  useTestMetadataKey,
  useUpdateMetadataSettings,
  type MetadataKeyService,
  type MetadataSettingsResource,
} from '../../api/metadata';
import { useSaveUpdateSettings, useUpdateSettings, type UpdateSettings } from '../../api/update';
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

interface KeyFieldProps {
  service: MetadataKeyService;
  label: string;
  usedFor: string;
  getKeyUrl: string;
  isSet: boolean;
  locked: boolean;
  value: string;
  onChange: (value: string) => void;
  onRemove: () => void;
  removing: boolean;
}

/** One optional key: a password field that never shows the stored key, a Test button, and where to get one. */
function KeyField({
  service,
  label,
  usedFor,
  getKeyUrl,
  isSet,
  locked,
  value,
  onChange,
  onRemove,
  removing,
}: KeyFieldProps) {
  const test = useTestMetadataKey();
  const typed = value.trim();

  return (
    <Stack gap="xs">
      <PasswordInput
        label={label}
        placeholder={locked ? 'Set by the environment' : isSet ? 'Stored — type to replace' : 'Not set'}
        value={value}
        disabled={locked}
        autoComplete="off"
        onChange={(event) => {
          onChange(event.currentTarget.value);
        }}
      />
      <Text size="sm" c="dimmed">
        {usedFor}{' '}
        <Anchor href={getKeyUrl} target="_blank" rel="noreferrer">
          Get a key
        </Anchor>
      </Text>
      <Group gap="xs">
        <Button
          variant="default"
          size="xs"
          aria-label={`Test ${label}`}
          loading={test.isPending}
          disabled={typed === '' && !isSet}
          onClick={() => {
            test.mutate({ service, key: typed === '' ? undefined : typed });
          }}
        >
          Test
        </Button>
        {isSet && !locked && (
          <Button
            variant="subtle"
            color="red"
            size="xs"
            aria-label={`Remove ${label}`}
            loading={removing}
            onClick={onRemove}
          >
            Remove
          </Button>
        )}
      </Group>
      {test.data !== undefined && (
        <Alert color={test.data.ok ? 'green' : 'red'} role="status" data-testid={`${service}-test-result`}>
          {test.data.message}
        </Alert>
      )}
      {test.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {test.error.message}
        </Alert>
      )}
    </Stack>
  );
}

function MetadataKeys({ settings }: { settings: MetadataSettingsResource }) {
  const update = useUpdateMetadataSettings();
  const [acoustId, setAcoustId] = useState('');
  const [lastFm, setLastFm] = useState('');
  const [saved, setSaved] = useState(false);

  const messages =
    update.error instanceof MetadataUpdateError && update.error.messages.length > 0
      ? update.error.messages
      : update.error !== null
        ? [update.error.message]
        : [];
  const changed = acoustId.trim() !== '' || lastFm.trim() !== '';

  function save(): void {
    setSaved(false);

    update.mutate(
      {
        // An empty field is left out, not sent: the stored key is only replaced by typing a new one.
        acoustIdClientKey: acoustId.trim() === '' ? undefined : acoustId.trim(),
        lastFmApiKey: lastFm.trim() === '' ? undefined : lastFm.trim(),
      },
      {
        onSuccess: () => {
          setAcoustId('');
          setLastFm('');
          setSaved(true);
        },
      },
    );
  }

  return (
    <Stack gap="md">
      <KeyField
        service="acoustid"
        label="AcoustID client key"
        usedFor="Fingerprint verification of downloads, and identifying the files of a reference library."
        getKeyUrl="https://acoustid.org/new-application"
        isSet={settings.acoustIdKeySet}
        locked={settings.acoustIdLocked}
        value={acoustId}
        onChange={setAcoustId}
        removing={update.isPending && update.variables.acoustIdClientKey === ''}
        onRemove={() => {
          setSaved(false);
          update.mutate({ acoustIdClientKey: '' });
        }}
      />
      <KeyField
        service="lastfm"
        label="Last.fm API key"
        usedFor="Facts about a song on its page, and the default key for Last.fm import lists."
        getKeyUrl="https://www.last.fm/api/account/create"
        isSet={settings.lastFmKeySet}
        locked={settings.lastFmLocked}
        value={lastFm}
        onChange={setLastFm}
        removing={update.isPending && update.variables.lastFmApiKey === ''}
        onRemove={() => {
          setSaved(false);
          update.mutate({ lastFmApiKey: '' });
        }}
      />
      {messages.length > 0 && (
        <Alert color="red" icon={<CircleAlert size={16} />} title="The keys could not be saved">
          {messages.join(' ')}
        </Alert>
      )}
      {saved && (
        <Alert color="green" role="status">
          Keys saved.
        </Alert>
      )}
      <Group>
        <Button loading={update.isPending && changed} disabled={!changed} onClick={save}>
          Save keys
        </Button>
      </Group>
    </Stack>
  );
}

function MetadataServicesCard() {
  const settings = useMetadataSettings();

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Title order={4}>Metadata services</Title>
        <Text size="sm" c="dimmed">
          Both keys are optional. They are stored in <Code>/config/config.yml</Code> and never shown again; a key set by{' '}
          <Code>APP__ACOUSTID__CLIENT_KEY</Code> or <Code>APP__LASTFM__API_KEY</Code> is read-only here.
        </Text>
        {settings.isPending ? (
          <LoadingState label="Loading the metadata keys…" />
        ) : settings.isError ? (
          <ErrorState title="The metadata keys could not be loaded" message={settings.error.message} />
        ) : (
          <MetadataKeys settings={settings.data} />
        )}
      </Stack>
    </Card>
  );
}

function UpdateCheckSwitch({ settings }: { settings: UpdateSettings }) {
  const save = useSaveUpdateSettings();

  return (
    <Stack gap="xs">
      <Switch
        label="Check for new versions on GitHub"
        description="Wondarr asks GitHub's public API twice a day whether a newer release exists. Nothing about your library is sent."
        checked={settings.checkEnabled}
        disabled={settings.checkEnabledLocked || save.isPending}
        onChange={(event) => save.mutate(event.currentTarget.checked)}
      />
      {settings.checkEnabledLocked && (
        <Text size="sm" c="dimmed">
          This is set by <Code>APP__UPDATE__CHECK_ENABLED</Code> and cannot be changed here.
        </Text>
      )}
      {save.isError && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {save.error.message}
        </Alert>
      )}
    </Stack>
  );
}

function UpdatesCard() {
  const settings = useUpdateSettings();

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Title order={4}>Updates</Title>
        {settings.isPending ? (
          <LoadingState label="Loading the update setting…" />
        ) : settings.isError ? (
          <ErrorState title="The update setting could not be loaded" message={settings.error.message} />
        ) : (
          <UpdateCheckSwitch settings={settings.data} />
        )}
      </Stack>
    </Card>
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

/** Settings → General: the API key, the login account, the optional metadata keys, and how this instance is reached. */
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
      <MetadataServicesCard />
      <UpdatesCard />
      <InstanceCard />
    </Stack>
  );
}
