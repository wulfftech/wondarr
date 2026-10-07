import {
  Alert,
  Button,
  Card,
  Group,
  PasswordInput,
  SegmentedControl,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import {
  useTestSoulseekConnection,
  useUpdateSoulseekSettings,
  type SoulseekSettingsResource,
} from '../../api/soulseek';

type Mode = 'bundled' | 'external';

/**
 * Which slskd Wondarr uses: the one it bundles and runs itself, or the user's own (external mode: a URL
 * and an API key). A mode change takes effect when Wondarr restarts; in external mode the account,
 * shares and transfer settings below belong to the user's slskd and are shown read-only.
 */
export function SoulseekConnectionCard({ settings }: { settings: SoulseekSettingsResource }) {
  const update = useUpdateSoulseekSettings();
  const test = useTestSoulseekConnection();
  const locked = new Set(settings.readOnlyFields);
  const [mode, setMode] = useState<Mode>(settings.mode === 'external' ? 'external' : 'bundled');
  const [url, setUrl] = useState(settings.externalUrl ?? '');
  const [apiKey, setApiKey] = useState('');
  const [rescan, setRescan] = useState(settings.externalRescanShares);
  const [restartNeeded, setRestartNeeded] = useState(false);

  const save = () => {
    update.mutate(
      {
        mode,
        externalUrl: mode === 'external' ? url.trim() : undefined,
        // Empty keeps the stored key; the field is never filled with it.
        externalApiKey: apiKey === '' ? undefined : apiKey,
        externalRescanShares: mode === 'external' ? rescan : undefined,
      },
      {
        onSuccess: (result) => {
          setApiKey('');
          setRestartNeeded(result.restartsWondarr);
        },
      },
    );
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Title order={4}>Connection</Title>

        <SegmentedControl
          value={mode}
          onChange={(value) => setMode(value === 'external' ? 'external' : 'bundled')}
          disabled={locked.has('mode')}
          data={[
            { label: 'Bundled slskd', value: 'bundled' },
            { label: 'My own slskd', value: 'external' },
          ]}
          data-testid="soulseek-mode"
        />

        {mode === 'external' && (
          <>
            <Text size="sm" c="dimmed">
              Wondarr searches and downloads through your slskd with its API key. The account, shares and transfer
              settings stay your slskd&apos;s: change them in slskd. Its download folder must be visible to Wondarr at
              the downloads directory below.
            </Text>
            <TextInput
              label="slskd URL"
              placeholder="http://slskd:5030"
              value={url}
              onChange={(event) => setUrl(event.currentTarget.value)}
              disabled={locked.has('externalUrl')}
            />
            <PasswordInput
              label="API key"
              placeholder={
                settings.externalApiKeySet ? 'Stored — type to replace' : 'From slskd: web.authentication.api_keys'
              }
              value={apiKey}
              onChange={(event) => setApiKey(event.currentTarget.value)}
              disabled={locked.has('externalApiKey')}
            />
            <Switch
              label="Ask slskd to rescan its shares after each import"
              checked={rescan}
              onChange={(event) => setRescan(event.currentTarget.checked)}
              disabled={locked.has('externalRescanShares')}
            />
          </>
        )}

        {test.data !== undefined && (
          <Alert
            color={test.data.ok && test.data.loggedIn ? 'green' : test.data.ok ? 'yellow' : 'red'}
            data-testid="soulseek-test-result"
          >
            {test.data.message}
          </Alert>
        )}
        {update.error !== null && (
          <Alert color="red" title="The connection could not be saved">
            {update.error.message}
          </Alert>
        )}
        {restartNeeded && (
          <Alert color="blue" data-testid="soulseek-restart-notice">
            Saved. Restart Wondarr to switch to {mode === 'external' ? 'your own slskd' : 'the bundled slskd'}.
          </Alert>
        )}

        <Group justify="flex-end">
          {mode === 'external' && (
            <Button
              variant="default"
              loading={test.isPending}
              disabled={url.trim() === ''}
              onClick={() => test.mutate({ url: url.trim(), apiKey: apiKey === '' ? null : apiKey })}
            >
              Test
            </Button>
          )}
          <Button loading={update.isPending} onClick={save}>
            Save connection
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}
