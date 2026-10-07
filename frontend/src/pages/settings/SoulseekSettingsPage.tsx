import {
  ActionIcon,
  Alert,
  Anchor,
  Button,
  Card,
  Group,
  NumberInput,
  PasswordInput,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, Lock, Plus, Trash2, TriangleAlert } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { SoulseekConnectionCard } from './SoulseekConnectionCard';
import {
  readSoulseekProblems,
  SoulseekUpdateError,
  useSoulseekSettings,
  useSoulseekStatus,
  useUpdateSoulseekSettings,
  type SoulseekFieldName,
  type SoulseekSettingsResource,
  type SoulseekSettingsUpdateResource,
  type SoulseekStatusResource,
} from '../../api/soulseek';

/** The environment-variable suffix each field is locked by, so the tooltip can name it. */
const ENV_SUFFIX: Record<SoulseekFieldName, string> = {
  username: 'USERNAME',
  password: 'PASSWORD',
  listenPort: 'LISTEN_PORT',
  shareLibrary: 'SHARE_LIBRARY',
  sharedFolders: 'SHARED_FOLDERS',
  uploadSlots: 'UPLOAD_SLOTS',
  uploadSpeedLimitKib: 'UPLOAD_SPEED_LIMIT_KIB',
  distributedNetwork: 'DISTRIBUTED_NETWORK',
  downloadsDir: 'DOWNLOADS_DIR',
  incompleteDir: 'INCOMPLETE_DIR',
};

/** The one-line explanation of each login problem the API can report. */
const LOGIN_PROBLEMS: Record<string, string> = {
  invalidCredentials: 'slskd could not log in: the Soulseek username or password was rejected.',
  duplicateLogin: 'slskd was kicked: another client logged in using the same username. Use a dedicated account.',
};

/** Wraps a locked field so the pointer still reaches a tooltip a disabled input would swallow. */
function Lockable({ field, locked, children }: { field: SoulseekFieldName; locked: boolean; children: ReactNode }) {
  if (!locked) {
    return children;
  }

  return (
    <Tooltip label={`Set by the environment variable APP__SOULSEEK__${ENV_SUFFIX[field]}`} withArrow multiline w={280}>
      <div>{children}</div>
    </Tooltip>
  );
}

/** A number the API sends as `number | string`; an empty box reads as 0. */
function toNumber(value: number | string): number {
  const parsed = Number(value);

  return Number.isFinite(parsed) ? parsed : 0;
}

/** The sharing line: the folder and file counts, or that sharing is off. */
function sharingText(status: SoulseekStatusResource): string {
  if (!status.sharing.enabled) {
    return 'Sharing is off';
  }

  const directories = status.sharing.directories === null ? '0' : String(status.sharing.directories);
  const files = status.sharing.files === null ? '0' : String(status.sharing.files);

  return `${directories} folders, ${files} files`;
}

/** The search allowance, with the time the next search may be submitted when one is pending. */
function budgetText(status: SoulseekStatusResource): string {
  const budget = status.searchBudget;
  const base = `${budget.submittedInWindow}/${budget.maxSearches} searches in the last 4 minutes, ${budget.outstanding} in flight`;

  if (budget.nextAllowedAt === null) {
    return base;
  }

  return `${base}; next at ${new Date(budget.nextAllowedAt).toLocaleTimeString()}`;
}

/** What slskd is doing: the login, the version and state, sharing and the search allowance. */
function StatusCard() {
  const status = useSoulseekStatus();

  if (status.isPending) {
    return <LoadingState label="Loading Soulseek status…" />;
  }

  if (status.error !== null) {
    return <ErrorState message={status.error.message} />;
  }

  const data = status.data;

  if (data === undefined) {
    return <EmptyState message="slskd has not reported anything yet." />;
  }

  const login =
    data.loginProblem === null
      ? data.loggedIn
        ? `Logged in as ${data.username ?? 'unknown'}`
        : data.username === null
          ? 'Not configured'
          : 'Logged out'
      : null;

  return (
    <Card withBorder padding="md">
      <Stack gap="xs">
        <Text fw={600}>slskd</Text>
        <Text size="sm">
          {data.version ?? 'version unknown'} · {data.state}
        </Text>

        {data.loginProblem !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />} title="Login problem">
            {LOGIN_PROBLEMS[data.loginProblem] ?? data.loginProblem}
          </Alert>
        )}

        {login !== null && <Text size="sm">{login}</Text>}

        <Text size="sm">{sharingText(data)}</Text>

        {data.lastError !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />} title="Last error">
            {data.lastError}
          </Alert>
        )}

        <Text size="sm" c="dimmed">
          {budgetText(data)}
        </Text>

        {data.pendingRestart && (
          <Text size="sm" c="orange">
            slskd restarts to apply the last change.
          </Text>
        )}
      </Stack>
    </Card>
  );
}

/** The Soulseek settings form. It is keyed on the settings it was mounted with, so a refetch starts it over. */
function SoulseekForm({ settings }: { settings: SoulseekSettingsResource }) {
  const save = useUpdateSoulseekSettings();
  const locked = new Set(settings.readOnlyFields);
  const isLocked = (field: SoulseekFieldName) => locked.has(field);

  const [username, setUsername] = useState(settings.username ?? '');
  const [password, setPassword] = useState('');
  const [passwordTouched, setPasswordTouched] = useState(false);
  const [listenPort, setListenPort] = useState<number | string>(settings.listenPort);
  const [shareLibrary, setShareLibrary] = useState(settings.shareLibrary);
  const [sharedFolders, setSharedFolders] = useState<string[]>([...settings.sharedFolders]);
  const [uploadSlots, setUploadSlots] = useState<number | string>(settings.uploadSlots);
  const [uploadSpeed, setUploadSpeed] = useState<number | string>(settings.uploadSpeedLimitKib);
  const [downloadsDir, setDownloadsDir] = useState(settings.downloadsDir);
  const [incompleteDir, setIncompleteDir] = useState(settings.incompleteDir);
  const [distributedNetwork, setDistributedNetwork] = useState(settings.distributedNetwork);

  // The server names the field in the message it refuses with; a message that names no field — a
  // read-only one, or the settings as a whole — goes under the form.
  const problems = save.error instanceof SoulseekUpdateError ? readSoulseekProblems(save.error.messages) : [];
  const fieldError = (field: SoulseekFieldName) => problems.find((problem) => problem.field === field)?.message;
  const general = problems.filter((problem) => problem.field === null);

  const lockIcon = (field: SoulseekFieldName) => (isLocked(field) ? <Lock size={16} /> : undefined);

  const submit = () => {
    // Only the fields this form owns travel: a field the environment sets is refused rather than
    // written, and the password is sent only when the user typed one or asked to clear it.
    const body: SoulseekSettingsUpdateResource = {};

    if (!isLocked('username')) {
      body.username = username;
    }

    if (passwordTouched) {
      body.password = password;
    }

    if (!isLocked('listenPort')) {
      body.listenPort = toNumber(listenPort);
    }

    if (!isLocked('shareLibrary')) {
      body.shareLibrary = shareLibrary;
    }

    if (!isLocked('sharedFolders')) {
      body.sharedFolders = sharedFolders;
    }

    if (!isLocked('uploadSlots')) {
      body.uploadSlots = toNumber(uploadSlots);
    }

    if (!isLocked('uploadSpeedLimitKib')) {
      body.uploadSpeedLimitKib = toNumber(uploadSpeed);
    }

    if (!isLocked('distributedNetwork')) {
      body.distributedNetwork = distributedNetwork;
    }

    if (!isLocked('downloadsDir')) {
      body.downloadsDir = downloadsDir;
    }

    if (!isLocked('incompleteDir')) {
      body.incompleteDir = incompleteDir;
    }

    save.mutate(body, {
      onSuccess: (result) =>
        notifications.show({
          message: result.restartsSlskd ? 'Saved — slskd restarts to apply it' : 'Saved',
          color: 'green',
        }),
    });
  };

  const setFolder = (index: number, value: string) =>
    setSharedFolders((folders) => folders.map((folder, at) => (at === index ? value : folder)));

  const addFolder = () => setSharedFolders((folders) => [...folders, '']);

  const removeFolder = (index: number) => setSharedFolders((folders) => folders.filter((_, at) => at !== index));

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Text fw={600}>Account</Text>

        <Lockable field="username" locked={isLocked('username')}>
          <TextInput
            label="Username"
            value={username}
            disabled={isLocked('username')}
            rightSection={lockIcon('username')}
            error={fieldError('username')}
            onChange={(event) => setUsername(event.currentTarget.value)}
          />
        </Lockable>

        <Lockable field="password" locked={isLocked('password')}>
          <PasswordInput
            label="Password"
            placeholder={settings.passwordSet ? '•••••••• (set)' : undefined}
            value={password}
            disabled={isLocked('password')}
            rightSection={lockIcon('password')}
            error={fieldError('password')}
            onChange={(event) => {
              setPassword(event.currentTarget.value);
              setPasswordTouched(true);
            }}
          />
        </Lockable>

        {!isLocked('password') && (
          <Anchor
            component="button"
            type="button"
            size="sm"
            onClick={() => {
              setPassword('');
              setPasswordTouched(true);
            }}
          >
            Clear password
          </Anchor>
        )}

        <Lockable field="listenPort" locked={isLocked('listenPort')}>
          <NumberInput
            label="Listen port"
            min={1024}
            max={65535}
            value={listenPort}
            disabled={isLocked('listenPort')}
            rightSection={lockIcon('listenPort')}
            error={fieldError('listenPort')}
            onChange={setListenPort}
          />
        </Lockable>

        <Text fw={600}>Sharing</Text>

        <Lockable field="shareLibrary" locked={isLocked('shareLibrary')}>
          <Switch
            label="Share my library"
            checked={shareLibrary}
            disabled={isLocked('shareLibrary')}
            error={fieldError('shareLibrary')}
            onChange={(event) => setShareLibrary(event.currentTarget.checked)}
          />
        </Lockable>

        {!shareLibrary && (
          <Alert color="yellow" icon={<TriangleAlert size={16} />}>
            Soulseek users often ban peers who share nothing. Downloads may fail or be refused.
          </Alert>
        )}

        <Stack gap="xs">
          <Text size="sm" fw={500}>
            Shared folders
          </Text>

          {sharedFolders.map((folder, index) => (
            <Group key={index} gap="xs" wrap="nowrap">
              <TextInput
                aria-label={`Shared folder ${index + 1}`}
                style={{ flex: 1 }}
                value={folder}
                disabled={isLocked('sharedFolders')}
                error={index === 0 ? fieldError('sharedFolders') : undefined}
                onChange={(event) => setFolder(index, event.currentTarget.value)}
              />
              <ActionIcon
                variant="subtle"
                color="red"
                aria-label={`Remove shared folder ${index + 1}`}
                disabled={isLocked('sharedFolders')}
                onClick={() => removeFolder(index)}
              >
                <Trash2 size={16} />
              </ActionIcon>
            </Group>
          ))}

          <Group gap="xs">
            <Button
              variant="light"
              size="xs"
              leftSection={<Plus size={14} />}
              disabled={isLocked('sharedFolders')}
              onClick={addFolder}
            >
              Add folder
            </Button>
            {isLocked('sharedFolders') && (
              <Text size="xs" c="dimmed">
                Set by the environment variable APP__SOULSEEK__SHARED_FOLDERS.
              </Text>
            )}
          </Group>
        </Stack>

        <Text fw={600}>Uploads</Text>

        <Lockable field="uploadSlots" locked={isLocked('uploadSlots')}>
          <NumberInput
            label="Upload slots"
            min={0}
            value={uploadSlots}
            disabled={isLocked('uploadSlots')}
            rightSection={lockIcon('uploadSlots')}
            error={fieldError('uploadSlots')}
            onChange={setUploadSlots}
          />
        </Lockable>

        <Lockable field="uploadSpeedLimitKib" locked={isLocked('uploadSpeedLimitKib')}>
          <NumberInput
            label="Upload speed limit (KiB/s)"
            description="0 means unlimited."
            min={0}
            value={uploadSpeed}
            disabled={isLocked('uploadSpeedLimitKib')}
            rightSection={lockIcon('uploadSpeedLimitKib')}
            error={fieldError('uploadSpeedLimitKib')}
            onChange={setUploadSpeed}
          />
        </Lockable>

        <Text fw={600}>Directories</Text>

        <Lockable field="downloadsDir" locked={isLocked('downloadsDir')}>
          <TextInput
            label="Downloads directory"
            value={downloadsDir}
            disabled={isLocked('downloadsDir')}
            rightSection={lockIcon('downloadsDir')}
            error={fieldError('downloadsDir')}
            onChange={(event) => setDownloadsDir(event.currentTarget.value)}
          />
        </Lockable>

        <Lockable field="incompleteDir" locked={isLocked('incompleteDir')}>
          <TextInput
            label="Incomplete directory"
            value={incompleteDir}
            disabled={isLocked('incompleteDir')}
            rightSection={lockIcon('incompleteDir')}
            error={fieldError('incompleteDir')}
            onChange={(event) => setIncompleteDir(event.currentTarget.value)}
          />
        </Lockable>

        <Text fw={600}>Network</Text>

        <Lockable field="distributedNetwork" locked={isLocked('distributedNetwork')}>
          <Switch
            label="Distributed network"
            checked={distributedNetwork}
            disabled={isLocked('distributedNetwork')}
            error={fieldError('distributedNetwork')}
            onChange={(event) => setDistributedNetwork(event.currentTarget.checked)}
          />
        </Lockable>

        {general.map((problem) => (
          <Alert key={problem.message} color="red" icon={<CircleAlert size={16} />}>
            {problem.message}
          </Alert>
        ))}

        {save.error !== null && problems.length === 0 && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {save.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button loading={save.isPending} onClick={submit}>
            Save
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}

/** Settings → Soulseek: which slskd (bundled or the user's own), its account, sharing and transfer settings, and its state. */
export function SoulseekSettingsPage() {
  const settings = useSoulseekSettings();

  return (
    <Stack gap="lg">
      <Title order={3}>Soulseek</Title>

      <StatusCard />

      {settings.isPending && <LoadingState />}

      {settings.error !== null && <ErrorState message={settings.error.message} />}

      {settings.data !== undefined && <SoulseekConnectionCard settings={settings.data} />}

      {settings.data !== undefined && (
        <SoulseekForm key={`${String(settings.data.username)}-${settings.data.mode}`} settings={settings.data} />
      )}
    </Stack>
  );
}
