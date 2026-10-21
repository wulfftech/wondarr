import { Alert, Anchor, Button, Card, Code, Group, List, PasswordInput, Stack, Text, Title } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import {
  useFeedbackSettings,
  useSaveFeedbackToken,
  useTestFeedbackToken,
  type FeedbackSettings,
} from '../../api/feedback';
import { ErrorState, LoadingState } from '../../components/DataState';

/**
 * GitHub's classic token page with the one scope ticked and a name filled in. Only a classic token may
 * open issues on someone else's public repository; GitHub ignores the parameters it does not know.
 */
export const CLASSIC_TOKEN_URL =
  'https://github.com/settings/tokens/new?scopes=public_repo&description=Wondarr%20feedback';

/** The fine-grained token page, for the repository's owner and collaborators. */
export const FINE_GRAINED_TOKEN_URL = 'https://github.com/settings/personal-access-tokens/new';

const REPOSITORY = 'wulfftech/wondarr';

function Instructions() {
  return (
    <Stack gap="xs">
      <Text size="sm" fw={500}>
        Make a token
      </Text>
      <List type="ordered" size="sm" spacing={4}>
        <List.Item>
          Signed in to GitHub, open{' '}
          <Anchor href={CLASSIC_TOKEN_URL} target="_blank" rel="noopener noreferrer" inherit>
            Generate new token (classic)
          </Anchor>
          . The link fills in the name and the scope.
        </List.Item>
        <List.Item>
          <b>Note:</b> anything you will recognise, such as <Code>Wondarr feedback</Code>. <b>Expiration:</b> your
          choice; when it runs out, feedback goes through GitHub&apos;s own issue form until you add a new token.
        </List.Item>
        <List.Item>
          Under <b>Select scopes</b>, tick <Code>public_repo</Code> and nothing else.
        </List.Item>
        <List.Item>
          Press <b>Generate token</b> and copy it: GitHub shows it only once. Paste it below, press <b>Test</b>, then{' '}
          <b>Save token</b>.
        </List.Item>
      </List>
      <Text size="xs" c="dimmed">
        Why classic: GitHub lets a fine-grained token open issues only on repositories you own or collaborate on. If you
        do, a{' '}
        <Anchor href={FINE_GRAINED_TOKEN_URL} target="_blank" rel="noopener noreferrer" inherit>
          fine-grained token
        </Anchor>{' '}
        is tighter: only <Code>{REPOSITORY}</Code>, with <b>Issues: Read and write</b>.
      </Text>
    </Stack>
  );
}

function TokenForm({ settings }: { settings: FeedbackSettings }) {
  const save = useSaveFeedbackToken();
  const test = useTestFeedbackToken();
  const [token, setToken] = useState('');
  const [saved, setSaved] = useState<'stored' | 'removed' | null>(null);
  const typed = token.trim();
  const locked = settings.tokenLocked;

  function store(value: string): void {
    setSaved(null);
    test.reset();

    save.mutate(value, {
      onSuccess: () => {
        setToken('');
        setSaved(value === '' ? 'removed' : 'stored');
      },
    });
  }

  return (
    <Stack gap="sm">
      <PasswordInput
        label="GitHub token"
        placeholder={locked ? 'Set by the environment' : settings.tokenSet ? 'Stored — paste to replace' : 'Not set'}
        value={token}
        disabled={locked}
        // "off" is ignored for a password field, and this page has the login password on it: a browser that
        // filled that in here would send it to GitHub as a token.
        autoComplete="new-password"
        spellCheck={false}
        onChange={(event) => {
          setToken(event.currentTarget.value);
          setSaved(null);
          // A result belongs to the token it was asked about, not to whatever is typed next.
          test.reset();
        }}
      />

      {locked && settings.tokenSet && (
        <Text size="sm" c="dimmed">
          Set by <Code>APP__FEEDBACK__GITHUB_TOKEN</Code>, so it cannot be changed here.
        </Text>
      )}
      {locked && !settings.tokenSet && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          <Code>APP__FEEDBACK__GITHUB_TOKEN</Code> is set but empty in Wondarr&apos;s environment, and it overrides this
          card. Remove it from the container&apos;s environment (or give it the token) to turn feedback on.
        </Alert>
      )}

      <Group gap="xs">
        <Button
          variant="default"
          loading={test.isPending}
          disabled={typed === '' && !settings.tokenSet}
          onClick={() => {
            test.mutate(typed === '' ? undefined : typed);
          }}
        >
          Test
        </Button>
        {!locked && (
          <Button loading={save.isPending && typed !== ''} disabled={typed === ''} onClick={() => store(typed)}>
            Save token
          </Button>
        )}
        {settings.tokenSet && !locked && (
          <Button variant="subtle" color="red" loading={save.isPending && typed === ''} onClick={() => store('')}>
            Remove
          </Button>
        )}
      </Group>

      {test.data !== undefined && (
        <Alert color={test.data.ok ? 'green' : 'red'} role="status" data-testid="feedback-token-test-result">
          {test.data.message}
        </Alert>
      )}
      {test.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {test.error.message}
        </Alert>
      )}
      {save.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />} title="The token could not be saved">
          {save.error.message}
        </Alert>
      )}
      {saved === 'stored' && (
        <Alert color="green" role="status">
          Token saved. The feedback button is in the header now, beside the theme toggle.
        </Alert>
      )}
      {saved === 'removed' && (
        <Alert color="green" role="status">
          Token removed. The feedback button is gone from the header.
        </Alert>
      )}
    </Stack>
  );
}

/** Settings → General → Contribute with feedback: the GitHub token that turns on the header's feedback button. */
export function FeedbackSettingsCard() {
  const settings = useFeedbackSettings();

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Title order={4}>Contribute with feedback</Title>
        <Text size="sm">
          Add a GitHub token and a feedback button appears in the header, beside the theme toggle. It sends a bug report
          or an idea, with the page you are on and your Wondarr version, to Wondarr&apos;s issue tracker on GitHub as a
          public issue under your GitHub name. Keys and tokens Wondarr knows are removed from what you write before it
          goes out.
        </Text>
        <Instructions />
        <Text size="xs" c="dimmed">
          What you are handing over: <Code>public_repo</Code> lets a token change any of your public repositories, not
          only open issues; Wondarr uses it for nothing but opening issues on <Code>{REPOSITORY}</Code>. The token is
          stored as plain text in <Code>/config/config.yml</Code> (readable only by Wondarr&apos;s user) and never shown
          again here. Give it an expiration, and revoke it on GitHub if you stop using it or move the config elsewhere.
        </Text>
        {settings.isPending ? (
          <LoadingState label="Loading the feedback settings…" />
        ) : settings.isError ? (
          <ErrorState title="The feedback settings could not be loaded" message={settings.error.message} />
        ) : (
          <TokenForm settings={settings.data} />
        )}
      </Stack>
    </Card>
  );
}
