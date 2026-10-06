import {
  Alert,
  Button,
  Card,
  Group,
  Modal,
  NumberInput,
  SegmentedControl,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, Lock, TriangleAlert } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import {
  readYouTubeProblems,
  useTestYouTube,
  useUpdateYouTubeSettings,
  useYouTubeSettings,
  useYouTubeStatus,
  YouTubeUpdateError,
  type YouTubeFieldName,
  type YouTubeSettingsResource,
  type YouTubeSettingsUpdateResource,
} from '../../api/youtube';

/** The localStorage key that remembers the one-time ToS acknowledgement (DECISIONS.md #6). */
const TOS_ACK_KEY = 'wondarr:youtube-tos-ack';

/** The environment-variable suffix each field is locked by, so the tooltip can name it. */
const ENV_SUFFIX: Record<YouTubeFieldName, string> = {
  enabled: 'ENABLED',
  cookiesPath: 'COOKIES_PATH',
  poTokenBaseUrl: 'PO_TOKEN_BASE_URL',
  allowVideos: 'ALLOW_VIDEOS',
  searchLimit: 'SEARCH_LIMIT',
  ytdlpSleepRequestsSeconds: 'YTDLP__SLEEP_REQUESTS_SECONDS',
  ytdlpSleepIntervalSeconds: 'YTDLP__SLEEP_INTERVAL_SECONDS',
  ytdlpMaxSleepIntervalSeconds: 'YTDLP__MAX_SLEEP_INTERVAL_SECONDS',
  ytdlpRetries: 'YTDLP__RETRIES',
};

/** The one-time acknowledgement, remembered in localStorage. */
function tosAcknowledged(): boolean {
  try {
    return window.localStorage.getItem(TOS_ACK_KEY) === '1';
  } catch {
    return false;
  }
}

function acknowledgeTos(): void {
  try {
    window.localStorage.setItem(TOS_ACK_KEY, '1');
  } catch {
    // Private mode or storage disabled: the disclaimer simply shows again next time.
  }
}

/** Wraps a locked field so the pointer still reaches a tooltip a disabled input would swallow. */
function Lockable({ field, locked, children }: { field: YouTubeFieldName; locked: boolean; children: ReactNode }) {
  if (!locked) {
    return children;
  }

  return (
    <Tooltip label={`Set by the environment variable APP__YOUTUBE__${ENV_SUFFIX[field]}`} withArrow multiline w={280}>
      <div>{children}</div>
    </Tooltip>
  );
}

/** A number the API sends as `number | string`; an empty box reads as 0. */
function toNumber(value: number | string): number {
  const parsed = Number(value);

  return Number.isFinite(parsed) ? parsed : 0;
}

/** The output policy's codec, as the library API's JSON carries it. */
type PolicyCodec = 'keep-opus' | 'aac' | 'mp3';

/** The output policy's mode, as the library API's JSON carries it. */
type PolicyMode = 'cbr' | 'vbr';

/** The default output policy form state, from the settings' JSON object. */
interface PolicyState {
  codec: PolicyCodec;
  mode: PolicyMode;
  bitrateKbps: number;
  vbrQuality: number;
  sampleRate: string;
}

/** Reads the policy the server sent into the form's state. The API carries it as a JSON element, so every member is checked and an unknown value falls back to the default. */
function policyState(policy: YouTubeSettingsResource['outputPolicy']): PolicyState {
  const source = (policy ?? {}) as Record<string, unknown>;
  const codec = typeof source.codec === 'string' ? source.codec : '';
  const mode = typeof source.mode === 'string' ? source.mode : '';
  const sampleRate = typeof source.sampleRate === 'string' ? source.sampleRate : '';

  return {
    codec: codec === 'aac' || codec === 'mp3' || codec === 'keep-opus' ? codec : 'aac',
    mode: mode === 'cbr' || mode === 'vbr' ? mode : 'cbr',
    bitrateKbps: typeof source.bitrateKbps === 'number' ? source.bitrateKbps : 256,
    vbrQuality: typeof source.vbrQuality === 'number' ? source.vbrQuality : 0,
    sampleRate: sampleRate === 'keep' || sampleRate === '44100' || sampleRate === '48000' ? sampleRate : 'keep',
  };
}

/** The status card: the cached probe's answer, and the Test button that runs a fresh one. */
function StatusCard() {
  const status = useYouTubeStatus();
  const test = useTestYouTube();

  const answer = test.data ?? status.data;

  return (
    <Card withBorder padding="md">
      <Stack gap="xs">
        <Group justify="space-between">
          <Text fw={600}>yt-dlp</Text>
          <Button variant="light" size="xs" loading={test.isPending} onClick={() => test.mutate()}>
            Test
          </Button>
        </Group>

        {answer === undefined ? (
          status.isPending ? (
            <LoadingState label="Loading the YouTube status…" />
          ) : status.error !== null ? (
            <ErrorState message={status.error.message} />
          ) : (
            <EmptyState message="The probe has not answered yet." />
          )
        ) : (
          <>
            <Text size="sm">
              {answer.binaryAvailable
                ? `yt-dlp ${answer.version ?? 'answered'}`
                : 'yt-dlp could not be run: it is not installed or not on PATH.'}
            </Text>

            <Text size="sm" c={answer.hasJsRuntime ? undefined : 'orange'}>
              {answer.hasJsRuntime
                ? 'The JS runtime (Deno) answered.'
                : 'The JS runtime (Deno) is missing: YouTube extraction will fail.'}
            </Text>
          </>
        )}

        {test.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {test.error.message}
          </Alert>
        )}
      </Stack>
    </Card>
  );
}

/** The YouTube settings form. Local state is seeded once; a save echoes back through the query cache. */
function YouTubeForm({ settings }: { settings: YouTubeSettingsResource }) {
  const save = useUpdateYouTubeSettings();
  const locked = new Set(settings.readOnlyFields);
  const isLocked = (field: YouTubeFieldName) => locked.has(field);

  const [enabled, setEnabled] = useState(settings.enabled);
  const [cookiesPath, setCookiesPath] = useState(settings.cookiesPath ?? '');
  const [poTokenBaseUrl, setPoTokenBaseUrl] = useState(settings.poTokenBaseUrl ?? '');
  const [allowVideos, setAllowVideos] = useState(settings.allowVideos);
  const [searchLimit, setSearchLimit] = useState<number | string>(settings.searchLimit);
  const [policy, setPolicy] = useState<PolicyState>(() => policyState(settings.outputPolicy));
  const [sleepRequests, setSleepRequests] = useState<number | string>(settings.ytdlp.sleepRequestsSeconds);
  const [sleepInterval, setSleepInterval] = useState<number | string>(settings.ytdlp.sleepIntervalSeconds);
  const [maxSleepInterval, setMaxSleepInterval] = useState<number | string>(settings.ytdlp.maxSleepIntervalSeconds);
  const [retries, setRetries] = useState<number | string>(settings.ytdlp.retries);

  // The one-time ToS disclaimer: shown the first time the user enables the source, remembered after.
  const [tosOpen, setTosOpen] = useState(false);

  const problems = save.error instanceof YouTubeUpdateError ? readYouTubeProblems(save.error.messages) : [];
  const fieldError = (field: YouTubeFieldName) => problems.find((problem) => problem.field === field)?.message;
  const general = problems.filter((problem) => problem.field === null);

  const lockIcon = (field: YouTubeFieldName) => (isLocked(field) ? <Lock size={16} /> : undefined);

  const submit = () => {
    // Only the fields this form owns travel: a field the environment sets is refused rather than
    // written, so it is left out of the body entirely.
    const body: YouTubeSettingsUpdateResource = {};

    if (!isLocked('enabled')) {
      body.enabled = enabled;
    }

    if (!isLocked('cookiesPath')) {
      body.cookiesPath = cookiesPath;
    }

    if (!isLocked('poTokenBaseUrl')) {
      body.poTokenBaseUrl = poTokenBaseUrl;
    }

    if (!isLocked('allowVideos')) {
      body.allowVideos = allowVideos;
    }

    if (!isLocked('searchLimit')) {
      body.searchLimit = toNumber(searchLimit);
    }

    if (!isLocked('ytdlpSleepRequestsSeconds') || !isLocked('ytdlpSleepIntervalSeconds') ||
        !isLocked('ytdlpMaxSleepIntervalSeconds') || !isLocked('ytdlpRetries')) {
      body.ytdlp = {
        sleepRequestsSeconds: isLocked('ytdlpSleepRequestsSeconds')
          ? undefined
          : toNumber(sleepRequests),
        sleepIntervalSeconds: isLocked('ytdlpSleepIntervalSeconds')
          ? undefined
          : toNumber(sleepInterval),
        maxSleepIntervalSeconds: isLocked('ytdlpMaxSleepIntervalSeconds')
          ? undefined
          : toNumber(maxSleepInterval),
        retries: isLocked('ytdlpRetries') ? undefined : toNumber(retries),
      };
    }

    body.outputPolicy = {
      codec: policy.codec,
      mode: policy.mode,
      bitrateKbps: policy.bitrateKbps,
      vbrQuality: policy.vbrQuality,
      sampleRate: policy.sampleRate,
    };

    save.mutate(body, {
      onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
    });
  };

  /** The enable toggle: the first enable shows the ToS disclaimer instead of flipping straight on. */
  const toggleEnabled = (checked: boolean) => {
    // Gated on the acknowledgement alone, not on the server's stored state: a source that was
    // enabled headlessly must still show the disclaimer to the first person who toggles it.
    if (checked && !tosAcknowledged()) {
      setTosOpen(true);

      return;
    }

    setEnabled(checked);
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Text fw={600}>Source</Text>

        <Lockable field="enabled" locked={isLocked('enabled')}>
          <Switch
            label="Enable the YouTube source"
            description="Off by default. Enabling it is your own choice, on your own egress."
            checked={enabled}
            disabled={isLocked('enabled')}
            error={fieldError('enabled')}
            onChange={(event) => toggleEnabled(event.currentTarget.checked)}
          />
        </Lockable>

        <Lockable field="allowVideos" locked={isLocked('allowVideos')}>
          <Switch
            label="Allow videos results"
            description="Official videos run longer than the recording (intros, outros); the duration tolerance still rejects those. Off keeps grabs to Art Tracks."
            checked={allowVideos}
            disabled={isLocked('allowVideos')}
            error={fieldError('allowVideos')}
            onChange={(event) => setAllowVideos(event.currentTarget.checked)}
          />
        </Lockable>

        <Lockable field="searchLimit" locked={isLocked('searchLimit')}>
          <NumberInput
            label="Search budget per song"
            description="How many searches one song may cost, the ISRC query included."
            min={1}
            max={50}
            value={searchLimit}
            disabled={isLocked('searchLimit')}
            rightSection={lockIcon('searchLimit')}
            error={fieldError('searchLimit')}
            onChange={setSearchLimit}
          />
        </Lockable>

        <Text fw={600}>Credentials</Text>

        <Lockable field="cookiesPath" locked={isLocked('cookiesPath')}>
          <TextInput
            label="Cookies file"
            description="A Netscape cookie file yt-dlp reads, for age-gated videos. Yours, not bundled."
            placeholder="/config/cookies.txt"
            value={cookiesPath}
            disabled={isLocked('cookiesPath')}
            rightSection={lockIcon('cookiesPath')}
            error={fieldError('cookiesPath')}
            onChange={(event) => setCookiesPath(event.currentTarget.value)}
          />
        </Lockable>

        <Lockable field="poTokenBaseUrl" locked={isLocked('poTokenBaseUrl')}>
          <TextInput
            label="PO-token provider"
            description="The base URL of your own bgutil PO-token sidecar, e.g. http://host:4416."
            placeholder="http://host:4416"
            value={poTokenBaseUrl}
            disabled={isLocked('poTokenBaseUrl')}
            rightSection={lockIcon('poTokenBaseUrl')}
            error={fieldError('poTokenBaseUrl')}
            onChange={(event) => setPoTokenBaseUrl(event.currentTarget.value)}
          />
        </Lockable>

        <Text fw={600}>Output policy (default)</Text>

        <Text size="sm" c="dimmed">
          What a YouTube download becomes before it is verified and placed — the default for libraries
          without a policy of their own. Whatever the container, the file is ranked as its Opus-160
          source, so a 320 cutoff keeps it upgradeable.
        </Text>

        <Stack gap="xs">
          <SegmentedControl
            aria-label="Output codec"
            data={[
              { value: 'aac', label: 'AAC' },
              { value: 'mp3', label: 'MP3' },
              { value: 'keep-opus', label: 'Keep Opus' },
            ]}
            value={policy.codec}
            onChange={(value) => setPolicy((state) => ({ ...state, codec: value }))}
          />

          {policy.codec !== 'keep-opus' && (
            <>
              <SegmentedControl
                aria-label="Output mode"
                data={[
                  { value: 'cbr', label: 'CBR' },
                  { value: 'vbr', label: 'VBR' },
                ]}
                value={policy.mode}
                onChange={(value) => setPolicy((state) => ({ ...state, mode: value }))}
              />

              {policy.mode === 'cbr' ? (
                <NumberInput
                  label="Bitrate (kbps)"
                  min={64}
                  max={320}
                  value={policy.bitrateKbps}
                  onChange={(value) => setPolicy((state) => ({ ...state, bitrateKbps: toNumber(value) }))}
                />
              ) : (
                <NumberInput
                  label="VBR quality (0 best – 9 worst)"
                  min={0}
                  max={9}
                  value={policy.vbrQuality}
                  onChange={(value) => setPolicy((state) => ({ ...state, vbrQuality: toNumber(value) }))}
                />
              )}

              <SegmentedControl
                aria-label="Sample rate"
                data={[
                  { value: 'keep', label: 'Keep' },
                  { value: '44100', label: '44.1 kHz' },
                  { value: '48000', label: '48 kHz' },
                ]}
                value={policy.sampleRate}
                onChange={(value) => setPolicy((state) => ({ ...state, sampleRate: value }))}
              />
            </>
          )}
        </Stack>

        <Text fw={600}>yt-dlp pacing</Text>

        <Lockable field="ytdlpSleepRequestsSeconds" locked={isLocked('ytdlpSleepRequestsSeconds')}>
          <NumberInput
            label="Seconds between requests"
            description="yt-dlp's --sleep-requests: the pause between HTTP requests during extraction."
            min={0}
            step={0.25}
            value={sleepRequests}
            disabled={isLocked('ytdlpSleepRequestsSeconds')}
            rightSection={lockIcon('ytdlpSleepRequestsSeconds')}
            error={fieldError('ytdlpSleepRequestsSeconds')}
            onChange={setSleepRequests}
          />
        </Lockable>

        <Lockable field="ytdlpSleepIntervalSeconds" locked={isLocked('ytdlpSleepIntervalSeconds')}>
          <NumberInput
            label="Sleep before each download"
            description="yt-dlp's --sleep-interval, with the maximum below as the upper bound."
            min={0}
            value={sleepInterval}
            disabled={isLocked('ytdlpSleepIntervalSeconds')}
            rightSection={lockIcon('ytdlpSleepIntervalSeconds')}
            error={fieldError('ytdlpSleepIntervalSeconds')}
            onChange={setSleepInterval}
          />
        </Lockable>

        <Lockable field="ytdlpMaxSleepIntervalSeconds" locked={isLocked('ytdlpMaxSleepIntervalSeconds')}>
          <NumberInput
            label="Maximum sleep"
            min={0}
            value={maxSleepInterval}
            disabled={isLocked('ytdlpMaxSleepIntervalSeconds')}
            rightSection={lockIcon('ytdlpMaxSleepIntervalSeconds')}
            error={fieldError('ytdlpMaxSleepIntervalSeconds')}
            onChange={setMaxSleepInterval}
          />
        </Lockable>

        <Lockable field="ytdlpRetries" locked={isLocked('ytdlpRetries')}>
          <NumberInput
            label="Retries"
            description="How many times yt-dlp itself retries a failed download."
            min={0}
            value={retries}
            disabled={isLocked('ytdlpRetries')}
            rightSection={lockIcon('ytdlpRetries')}
            error={fieldError('ytdlpRetries')}
            onChange={setRetries}
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

      <Modal
        opened={tosOpen}
        onClose={() => setTosOpen(false)}
        title={
          <Group gap="xs">
            <TriangleAlert size={18} />
            <Text fw={600}>Before you enable YouTube</Text>
          </Group>
        }
        centered
      >
        <Stack gap="md">
          <Text size="sm">
            YouTube's Terms of Service prohibit automated access. Wondarr does not bundle an account,
            cookies or a token: you enable this with your own credentials, on your own egress, and
            the pacing stays well inside YouTube's tolerance.
          </Text>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTosOpen(false)}>
              Keep it off
            </Button>
            <Button
              onClick={() => {
                acknowledgeTos();
                setTosOpen(false);
                setEnabled(true);
              }}
            >
              I understand
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  );
}

/** Settings → YouTube: the source's enable toggle, credentials, output policy and yt-dlp pacing. */
export function YouTubeSettingsPage() {
  const settings = useYouTubeSettings();

  return (
    <Stack gap="lg">
      <Title order={3}>YouTube</Title>

      <StatusCard />

      {settings.isPending && <LoadingState />}

      {settings.error !== null && <ErrorState message={settings.error.message} />}

      {settings.data !== undefined && <YouTubeForm settings={settings.data} />}
    </Stack>
  );
}
