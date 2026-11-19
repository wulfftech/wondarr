import {
  Alert,
  Button,
  Card,
  Group,
  NumberInput,
  Select,
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
import {
  NAMING_TOKENS,
  useConfirmMusicVideoRoot,
  useMusicVideoSettings,
  useUpdateMusicVideoSettings,
  type MusicVideoFieldName,
  type MusicVideoSettingsResource,
  type MusicVideoSettingsUpdateResource,
} from '../../api/musicVideos';
import { useMusicVideoMatchSummary, useScanMusicVideos } from '../../api/musicVideoMatch';
import { ValidationError } from '../../api/profiles';
import { ErrorState, LoadingState } from '../../components/DataState';
import { FolderPicker } from '../../components/FolderPicker';

/** What the server says when the folder is saved but music videos wait for it to be confirmed. */
const CONFIRM_FIRST = /confirm the folder first/i;

/** The environment-variable suffix each field is locked by, so the tooltip can name it. */
const ENV_SUFFIX: Record<MusicVideoFieldName, string> = {
  enabled: 'ENABLED',
  rootPath: 'ROOT_PATH',
  layout: 'LAYOUT',
  namingTemplate: 'NAMING_TEMPLATE',
  besideTrackSuffix: 'BESIDE_TRACK_SUFFIX',
  maxHeight: 'MAX_HEIGHT',
  writeNfo: 'WRITE_NFO',
  writeThumbnail: 'WRITE_THUMBNAIL',
  scheduledSearchLimit: 'SCHEDULED_SEARCH_LIMIT',
  scheduledSearchEnabled: 'SCHEDULED_SEARCH_ENABLED',
  scheduledSearchIntervalHours: 'SCHEDULED_SEARCH_INTERVAL_HOURS',
};

const LAYOUTS = [
  { value: 'folder', label: 'Separate folder (Jellyfin, Kodi, Plex Other Videos)' },
  { value: 'beside_track', label: 'Beside each track (Plex)' },
];

/** The height caps on offer. 0 is the API's spelling of "no cap". */
const HEIGHTS = [
  { value: '0', label: 'Any' },
  { value: '2160', label: '2160' },
  { value: '1440', label: '1440' },
  { value: '1080', label: '1080' },
  { value: '720', label: '720' },
];

/** Wraps a locked field so the pointer still reaches a tooltip a disabled input would swallow. */
function Lockable({ field, locked, children }: { field: MusicVideoFieldName; locked: boolean; children: ReactNode }) {
  if (!locked) {
    return children;
  }

  return (
    <Tooltip
      label={`Set by the environment variable APP__MUSIC_VIDEOS__${ENV_SUFFIX[field]}`}
      withArrow
      multiline
      w={280}
    >
      <div>{children}</div>
    </Tooltip>
  );
}

/** A number the API sends as `number | string`; an empty box reads as 0. */
function toNumber(value: number | string): number {
  const parsed = Number(value);

  return Number.isFinite(parsed) ? parsed : 0;
}

/** What the root check found, said so a person can act on it. */
function rootProblem(status: MusicVideoSettingsResource['rootStatus']): string {
  if (status === 'missing') {
    return 'That folder does not exist yet.';
  }

  if (status === 'unmarked') {
    return 'That folder has files in it but no Wondarr marker, so Wondarr does not know it is the right place.';
  }

  return 'That folder is not ready.';
}

/** The music video settings form. Local state is seeded once; a save echoes back through the query cache. */
function MusicVideoForm({ settings }: { settings: MusicVideoSettingsResource }) {
  const save = useUpdateMusicVideoSettings();
  const confirm = useConfirmMusicVideoRoot();
  const locked = new Set(settings.readOnlyFields);
  const isLocked = (field: MusicVideoFieldName) => locked.has(field);

  const [enabled, setEnabled] = useState(settings.enabled);
  const [rootPath, setRootPath] = useState(settings.rootPath ?? '');
  const [picking, setPicking] = useState(false);
  // Turning on was asked for, and waits for the folder to be confirmed.
  const [pendingEnable, setPendingEnable] = useState(false);
  const [layout, setLayout] = useState(settings.layout);
  const [namingTemplate, setNamingTemplate] = useState(settings.namingTemplate);
  const [besideTrackSuffix, setBesideTrackSuffix] = useState(settings.besideTrackSuffix);
  const [maxHeight, setMaxHeight] = useState(String(settings.maxHeight ?? 0));
  const [writeNfo, setWriteNfo] = useState(settings.writeNfo);
  const [writeThumbnail, setWriteThumbnail] = useState(settings.writeThumbnail);
  const [scheduledEnabled, setScheduledEnabled] = useState(settings.scheduledSearchEnabled);
  const [interval, setInterval] = useState<number | string>(settings.scheduledSearchIntervalHours);
  const [limit, setLimit] = useState<number | string>(settings.scheduledSearchLimit);

  const fields = save.error instanceof ValidationError ? save.error.fields : {};
  const fieldError = (field: MusicVideoFieldName) => fields[field];
  const known = new Set<string>(Object.keys(ENV_SUFFIX));
  // Whatever the server says about no single field, or a field this form does not show.
  const general = Object.entries(fields)
    .filter(([field]) => !known.has(field))
    .map(([, message]) => message);

  const noFolder = rootPath.trim() === '';
  const defaultRoot = settings.defaultRootPath;
  const isDefault = defaultRoot !== null && defaultRoot !== undefined && rootPath === defaultRoot;
  // "On" needs a folder; the server says the same, this just says it before the save.
  const folderError =
    fieldError('rootPath') ??
    (enabled && noFolder ? 'Choose a video folder before turning music videos on.' : undefined);

  const lockIcon = (field: MusicVideoFieldName) => (isLocked(field) ? <Lock size={16} /> : undefined);

  // A cap the page does not offer (set in config.yml) still shows, so saving never silently changes it.
  const heights = HEIGHTS.some((height) => height.value === maxHeight)
    ? HEIGHTS
    : [...HEIGHTS, { value: maxHeight, label: maxHeight }];

  const submit = () => {
    // A field the environment sets is refused rather than written, so it stays out of the body.
    const body: MusicVideoSettingsUpdateResource = {};

    if (!isLocked('enabled')) {
      body.enabled = enabled;
    }

    if (!isLocked('rootPath') && rootPath.trim() !== (settings.rootPath ?? '')) {
      // An empty folder is the API's spelling of "back to the default".
      body.rootPath = rootPath === defaultRoot ? '' : rootPath.trim();
    }

    if (!isLocked('layout')) {
      body.layout = layout;
    }

    if (!isLocked('namingTemplate')) {
      body.namingTemplate = namingTemplate;
    }

    if (!isLocked('besideTrackSuffix') && layout === 'beside_track') {
      body.besideTrackSuffix = besideTrackSuffix;
    }

    if (!isLocked('maxHeight')) {
      body.maxHeight = Number(maxHeight);
    }

    if (!isLocked('writeNfo')) {
      body.writeNfo = writeNfo;
    }

    if (!isLocked('writeThumbnail')) {
      body.writeThumbnail = writeThumbnail;
    }

    if (!isLocked('scheduledSearchEnabled')) {
      body.scheduledSearchEnabled = scheduledEnabled;
    }

    if (!isLocked('scheduledSearchIntervalHours')) {
      body.scheduledSearchIntervalHours = toNumber(interval);
    }

    if (!isLocked('scheduledSearchLimit')) {
      body.scheduledSearchLimit = toNumber(limit);
    }

    save.mutate(body, {
      onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
      onError: (error) => {
        if (error instanceof ValidationError && CONFIRM_FIRST.test(error.fields.rootPath ?? '')) {
          // The folder is saved, the switch is not: it reads as off until the folder is confirmed.
          setEnabled(false);
          setPendingEnable(true);
        }
      },
    });
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Text fw={600}>Music videos</Text>

        <Lockable field="enabled" locked={isLocked('enabled')}>
          <Switch
            label="Find and download music videos"
            description="Off by default. Adds a video column to the Library and Wanted tables and a video tab to the song page."
            checked={enabled}
            disabled={isLocked('enabled') || (noFolder && !enabled)}
            error={fieldError('enabled')}
            onChange={(event) => setEnabled(event.currentTarget.checked)}
          />
        </Lockable>

        <Lockable field="rootPath" locked={isLocked('rootPath')}>
          <Stack gap="xs">
            <TextInput
              label="Video folder"
              description="A mounted folder outside your libraries. Videos go here, and so do songs held only by a reference library."
              placeholder="No folder chosen"
              value={rootPath}
              readOnly
              disabled={isLocked('rootPath')}
              rightSection={
                isLocked('rootPath') ? (
                  <Lock size={16} />
                ) : isDefault ? (
                  <Text size="xs" c="dimmed">
                    (default)
                  </Text>
                ) : undefined
              }
              rightSectionWidth={isDefault && !isLocked('rootPath') ? 64 : undefined}
              error={folderError}
            />

            {!isLocked('rootPath') && (
              <Group gap="xs">
                <Button size="xs" variant="default" onClick={() => setPicking(true)}>
                  Choose…
                </Button>

                {defaultRoot !== null && defaultRoot !== undefined && !isDefault && (
                  <Button size="xs" variant="subtle" onClick={() => setRootPath(defaultRoot)}>
                    Use default
                  </Button>
                )}

                {(defaultRoot === null || defaultRoot === undefined) && noFolder && (
                  <Text size="xs" c="dimmed">
                    No default here: choose a mounted folder
                  </Text>
                )}
              </Group>
            )}
          </Stack>
        </Lockable>

        {picking && (
          <FolderPicker
            opened
            initialPath={noFolder ? null : rootPath}
            onClose={() => setPicking(false)}
            onSelect={(path) => setRootPath(path)}
          />
        )}

        {settings.rootNeedsConfirmation && (enabled || pendingEnable) && (
          <Alert color="yellow" icon={<TriangleAlert size={16} />} title="Is this the right folder?">
            <Stack gap="xs" align="flex-start">
              <Text size="sm">
                {rootProblem(settings.rootStatus)} Wondarr writes a marker file into the folder, creating it if it is
                missing, before it puts any video there.
              </Text>

              {confirm.error !== null && (
                <Text size="sm" c="red">
                  {confirm.error.message}
                </Text>
              )}

              <Button
                size="xs"
                color="yellow"
                loading={confirm.isPending}
                onClick={() =>
                  confirm.mutate(undefined, {
                    onSuccess: () => {
                      notifications.show({ message: 'Video folder confirmed', color: 'green' });

                      // The person asked for music videos on; the folder was the only thing in the way.
                      if (pendingEnable) {
                        setPendingEnable(false);
                        setEnabled(true);
                        save.mutate({ enabled: true });
                      }
                    },
                  })
                }
              >
                Yes, use this folder
              </Button>
            </Stack>
          </Alert>
        )}

        <Lockable field="layout" locked={isLocked('layout')}>
          <Select
            label="Layout"
            description={
              layout === 'beside_track'
                ? 'A video sits next to its audio file, named after it. Songs held only by a reference library use the separate folder.'
                : 'Videos live in their own folder, which you point a Jellyfin, Kodi or Plex music video library at.'
            }
            data={LAYOUTS}
            value={layout}
            allowDeselect={false}
            disabled={isLocked('layout')}
            rightSection={lockIcon('layout')}
            error={fieldError('layout')}
            onChange={(value) => setLayout(value ?? 'folder')}
          />
        </Lockable>

        <Lockable field="namingTemplate" locked={isLocked('namingTemplate')}>
          <TextInput
            label="Naming template"
            description={`The path of a video under the folder, without its extension. Tokens: ${NAMING_TOKENS.join(', ')}.`}
            value={namingTemplate}
            disabled={isLocked('namingTemplate')}
            rightSection={lockIcon('namingTemplate')}
            error={fieldError('namingTemplate')}
            onChange={(event) => setNamingTemplate(event.currentTarget.value)}
          />
        </Lockable>

        {layout === 'beside_track' && (
          <Lockable field="besideTrackSuffix" locked={isLocked('besideTrackSuffix')}>
            <TextInput
              label="Beside-track suffix"
              description="Added to the audio file's name to name its video, e.g. “-video”."
              value={besideTrackSuffix}
              disabled={isLocked('besideTrackSuffix')}
              rightSection={lockIcon('besideTrackSuffix')}
              error={fieldError('besideTrackSuffix')}
              onChange={(event) => setBesideTrackSuffix(event.currentTarget.value)}
            />
          </Lockable>
        )}

        <Lockable field="maxHeight" locked={isLocked('maxHeight')}>
          <Select
            label="Maximum height"
            description="The tallest picture to download, in pixels. Above 1080 YouTube mostly serves VP9 or AV1."
            data={heights}
            value={maxHeight}
            allowDeselect={false}
            disabled={isLocked('maxHeight')}
            rightSection={lockIcon('maxHeight')}
            error={fieldError('maxHeight')}
            onChange={(value) => setMaxHeight(value ?? '0')}
          />
        </Lockable>

        <Lockable field="writeNfo" locked={isLocked('writeNfo')}>
          <Switch
            label="Write .nfo"
            description="A Kodi and Jellyfin metadata file beside each video."
            checked={writeNfo}
            disabled={isLocked('writeNfo')}
            error={fieldError('writeNfo')}
            onChange={(event) => setWriteNfo(event.currentTarget.checked)}
          />
        </Lockable>

        <Lockable field="writeThumbnail" locked={isLocked('writeThumbnail')}>
          <Switch
            label="Save thumbnail"
            description="YouTube's thumbnail, saved beside each video."
            checked={writeThumbnail}
            disabled={isLocked('writeThumbnail')}
            error={fieldError('writeThumbnail')}
            onChange={(event) => setWriteThumbnail(event.currentTarget.checked)}
          />
        </Lockable>

        <Text fw={600}>Scheduled search</Text>

        <Lockable field="scheduledSearchEnabled" locked={isLocked('scheduledSearchEnabled')}>
          <Switch
            label="Find missing videos on a schedule"
            description="Downloads only confident matches. Anything less waits for you to pick it."
            checked={scheduledEnabled}
            disabled={isLocked('scheduledSearchEnabled')}
            error={fieldError('scheduledSearchEnabled')}
            onChange={(event) => setScheduledEnabled(event.currentTarget.checked)}
          />
        </Lockable>

        <Lockable field="scheduledSearchIntervalHours" locked={isLocked('scheduledSearchIntervalHours')}>
          <NumberInput
            label="Run every (hours)"
            min={1}
            value={interval}
            disabled={isLocked('scheduledSearchIntervalHours')}
            rightSection={lockIcon('scheduledSearchIntervalHours')}
            error={fieldError('scheduledSearchIntervalHours')}
            onChange={setInterval}
          />
        </Lockable>

        <Lockable field="scheduledSearchLimit" locked={isLocked('scheduledSearchLimit')}>
          <NumberInput
            label="Songs per run"
            description="MusicBrainz answers one request a second, so a run stays small on purpose."
            min={1}
            value={limit}
            disabled={isLocked('scheduledSearchLimit')}
            rightSection={lockIcon('scheduledSearchLimit')}
            error={fieldError('scheduledSearchLimit')}
            onChange={setLimit}
          />
        </Lockable>

        {general.map((message) => (
          <Alert key={message} color="red" icon={<CircleAlert size={16} />}>
            {message}
          </Alert>
        ))}

        {save.error !== null && !(save.error instanceof ValidationError && Object.keys(fields).length > 0) && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {save.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button loading={save.isPending} disabled={enabled && noFolder} onClick={submit}>
            Save
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}

/** What the scan says it found, in the order a person reads it. */
const SUMMARY_PARTS: {
  key: 'adopted' | 'ambiguous' | 'unmatched' | 'ignored' | 'missing' | 'pending';
  label: string;
}[] = [
  { key: 'adopted', label: 'matched' },
  { key: 'ambiguous', label: 'need a pick' },
  { key: 'unmatched', label: 'no match' },
  { key: 'ignored', label: 'ignored' },
  { key: 'missing', label: 'missing' },
  { key: 'pending', label: 'waiting to be read' },
];

/** Reads the video folder for videos Wondarr did not download, and says what it found. */
function ScanCard({ settings }: { settings: MusicVideoSettingsResource }) {
  const scan = useScanMusicVideos();
  const ready = settings.enabled && settings.rootStatus === 'ready' && !settings.rootNeedsConfirmation;
  const summary = useMusicVideoMatchSummary(settings.enabled);
  const counts = summary.data;

  return (
    <Card withBorder padding="md">
      <Stack gap="sm" align="flex-start">
        <Text fw={600}>Videos already in the folder</Text>

        <Text size="sm">
          Wondarr reads the video folder on its own once a day. Scan it now after you add files. A video it can match to
          one song is kept where it is; the rest wait on the Match page.
        </Text>

        {counts !== undefined && (
          <Text size="sm" c="dimmed">
            {SUMMARY_PARTS.map((part) => `${String(counts[part.key])} ${part.label}`).join(' · ')}
          </Text>
        )}

        {!ready && (
          <Text size="sm" c="dimmed">
            {settings.enabled
              ? 'Confirm the video folder to scan it.'
              : 'Turn music videos on and save to scan the folder.'}
          </Text>
        )}

        {scan.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {scan.error.message}
          </Alert>
        )}

        <Button
          variant="default"
          disabled={!ready}
          loading={scan.isPending}
          onClick={() =>
            scan.mutate(undefined, {
              onSuccess: () => notifications.show({ message: 'Scanning the video folder', color: 'blue' }),
            })
          }
        >
          Scan the folder now
        </Button>
      </Stack>
    </Card>
  );
}

/** Settings → Music videos: the feature's switch, its folder and layout, and the scheduled search. */
export function MusicVideoSettingsPage() {
  const settings = useMusicVideoSettings();

  return (
    <Stack gap="lg">
      <Title order={3}>Music videos</Title>

      {settings.isPending && <LoadingState />}

      {settings.error !== null && <ErrorState message={settings.error.message} />}

      {settings.data !== undefined && <MusicVideoForm settings={settings.data} />}

      {settings.data !== undefined && <ScanCard settings={settings.data} />}
    </Stack>
  );
}
