import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Checkbox,
  Group,
  Modal,
  NumberInput,
  Popover,
  Select,
  Stack,
  Switch,
  Tabs,
  Text,
  TextInput,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { CircleAlert, TriangleAlert } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { useRunCommand } from '../../api/hooks';
import { usePlexSections, usePlexState } from '../../api/plex';
import type { CommandRequest } from '../../api/types';
import {
  ALBUM_POLICIES,
  LIBRARY_LAYOUTS,
  readEnum,
  readOutputPolicy,
  useCreateLibrary,
  useDeleteLibrary,
  useLibraries,
  useNamingPreview,
  useSaveLibrary,
  ValidationError,
  writeOutputPolicy,
  type AlbumPolicyName,
  type LibraryLayoutName,
  type LibraryResource,
  type OutputPolicy,
} from '../../api/profiles';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { CompactLibraryModal } from './CompactLibraryModal';
import { ConvertLibraryModal } from './ConvertLibraryModal';
import { OutputRulesEditor } from './OutputRulesEditor';

/** The members the API's validation messages can be attached to. */
const KNOWN_FIELDS = new Set([
  'id',
  'name',
  'rootPath',
  'layout',
  'namingTemplate',
  'sidecarOptions',
  'albumPolicy',
  'minTracksPerRealAlbum',
  'plexSectionId',
  'plexLibraryPath',
  'isDefault',
]);

/** The template tokens the helper lists, in the order a path is built from them (LIBRARY_OUTPUT §7.1). */
const TEMPLATE_TOKENS = [
  '{Artist Name}',
  '{Album Artist Name}',
  '{Album Title}',
  '{Release Year}',
  '{track:00}',
  '{medium:0}',
  '{Track Title}',
  '{Track ArtistName}',
  '{Quality Title}',
  '{Artist NameThe}',
  '{Artist CleanName}',
  '[ ({Release Year})]',
];

/** The default template of each layout preset (LIBRARY_OUTPUT §7.1). */
const LAYOUT_TEMPLATES: Record<LibraryLayoutName, string> = {
  flat: '{Artist Name} - {Track Title}',
  artist: '{Artist Name}/{Artist Name} - {Track Title}',
  artistAlbum: '{Artist Name}/{Album Title} ({Release Year})/{track:00} - {Track Title}',
  plexamp: '{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}',
};

/** How long typing settles before the preview is asked for, in milliseconds. */
const PREVIEW_DELAY_MS = 400;

const LAYOUT_OPTIONS = LIBRARY_LAYOUTS.map((layout) => ({ value: layout.value, label: layout.label }));

/**
 * The naming template field: the input, a token helper that inserts at the cursor, a button that
 * restores the selected layout's preset, and a preview of the path the template renders to.
 */
function NamingTemplateField({
  libraryId,
  layout,
  value,
  error,
  onChange,
}: {
  libraryId: number;
  layout: LibraryLayoutName;
  value: string;
  error?: string;
  onChange: (template: string) => void;
}) {
  const preview = useNamingPreview();
  const [tokensOpen, setTokensOpen] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const caret = useRef<number | null>(null);
  const [debounced] = useDebouncedValue(value, PREVIEW_DELAY_MS);
  const { mutate } = preview;

  // The preview follows what the user stopped typing, not every keystroke.
  useEffect(() => {
    mutate({ id: libraryId, template: debounced });
  }, [mutate, libraryId, debounced]);

  // A token is inserted where the caret was; the DOM only has the new value after the re-render.
  useEffect(() => {
    if (caret.current !== null && inputRef.current !== null) {
      inputRef.current.setSelectionRange(caret.current, caret.current);
      caret.current = null;
    }
  }, [value]);

  const insertToken = (token: string) => {
    const input = inputRef.current;
    const start = input?.selectionStart ?? value.length;
    const end = input?.selectionEnd ?? start;

    caret.current = start + token.length;
    onChange(`${value.slice(0, start)}${token}${value.slice(end)}`);
    setTokensOpen(false);
  };

  const layoutLabel = LIBRARY_LAYOUTS.find((candidate) => candidate.value === layout)?.label ?? layout;

  const previewErrors = preview.data?.errors ?? [];
  const previewPath = preview.data?.path ?? null;

  return (
    <Stack gap="xs">
      <TextInput
        ref={inputRef}
        label="Naming template"
        value={value}
        error={error}
        onChange={(event) => onChange(event.currentTarget.value)}
      />

      <Group gap="xs">
        <Popover opened={tokensOpen} onDismiss={() => setTokensOpen(false)} position="bottom-start" shadow="md">
          <Popover.Target>
            <Button variant="light" size="xs" onClick={() => setTokensOpen((open) => !open)}>
              Tokens
            </Button>
          </Popover.Target>
          <Popover.Dropdown>
            <Stack gap={4}>
              {TEMPLATE_TOKENS.map((token) => (
                <Anchor component="button" type="button" key={token} size="sm" onClick={() => insertToken(token)}>
                  {token}
                </Anchor>
              ))}
            </Stack>
          </Popover.Dropdown>
        </Popover>

        <Button variant="light" size="xs" onClick={() => onChange(LAYOUT_TEMPLATES[layout])}>
          Reset to the {layoutLabel} preset
        </Button>
      </Group>

      {previewErrors.length > 0 ? (
        <Text size="xs" c="red">
          {previewErrors.join(' ')}
        </Text>
      ) : preview.error !== null ? (
        <Text size="xs" c="red">
          {preview.error.message}
        </Text>
      ) : previewPath !== null ? (
        <Text size="xs" c="dimmed" ff="monospace">
          Preview: {previewPath}
        </Text>
      ) : (
        <Text size="xs" c="dimmed">
          Preview: {preview.isPending ? 'rendering…' : 'nothing to show'}
        </Text>
      )}
    </Stack>
  );
}

/** The `localStorage` key the one-time Plex notice is remembered under, one per library. */
const PLEX_NOTICE_KEY_PREFIX = 'wondarr.plexNotice.dismissed.';

/**
 * Whether this library's Plex notice has been dismissed, and how to dismiss it. The flag is read
 * once per mount, so a dismissal survives re-renders and reloads; a browser that refuses storage
 * (`localStorage` throws rather than being absent) only keeps it for the session.
 */
function usePlexNoticeDismissal(libraryId: number): { dismissed: boolean; dismiss: () => void } {
  const [dismissed, setDismissed] = useState(() => {
    try {
      return localStorage.getItem(`${PLEX_NOTICE_KEY_PREFIX}${libraryId}`) === '1';
    } catch {
      return false;
    }
  });

  const dismiss = () => {
    setDismissed(true);

    try {
      localStorage.setItem(`${PLEX_NOTICE_KEY_PREFIX}${libraryId}`, '1');
    } catch {
      // No storage: the notice comes back with the next reload, which is the safe way to fail.
    }
  };

  return { dismissed, dismiss };
}

/**
 * The Plex block of one library: the music section it feeds, where Plex sees its root folder, and
 * the one-time notice about the scanner's "Prefer local metadata" setting (LIBRARY_OUTPUT §7.2).
 */
function PlexBlock({
  libraryId,
  rootPath,
  layout,
  sectionId,
  libraryPath,
  onSectionId,
  onLibraryPath,
}: {
  libraryId: number;
  rootPath: string;
  layout: LibraryLayoutName;
  sectionId: string | null;
  libraryPath: string;
  onSectionId: (id: string | null) => void;
  onLibraryPath: (path: string) => void;
}) {
  const navigate = useNavigate();
  const plex = usePlexState();
  const hasServer = plex.data?.serverUrl != null;
  const sections = usePlexSections(hasServer);
  const notice = usePlexNoticeDismissal(libraryId);

  // The scanner keys on the folder, so the section's own first location is the placeholder the
  // user's answer is compared against.
  const locations = new Map((sections.data ?? []).map((section) => [section.key, section.locations[0] ?? null]));
  const placeholder = sectionId === null ? null : (locations.get(sectionId) ?? null);

  return (
    <Stack gap="xs">
      <Text fw={600}>Plex</Text>

      {!notice.dismissed && (
        <Alert
          color="yellow"
          icon={<TriangleAlert size={16} />}
          withCloseButton
          closeButtonLabel="Dismiss"
          onClose={notice.dismiss}
        >
          Plex groups and names albums by their tags only when <strong>Prefer local metadata</strong> is on. Turn it on
          (and <strong>Use local assets</strong>) in the Plex library&apos;s advanced settings before the first scan, or
          Plex may rename or split albums.
          {(layout === 'flat' || layout === 'artist') && ' A flat or artist-only layout depends on it completely.'}
        </Alert>
      )}

      {hasServer ? (
        <>
          <Select
            label="Plex music section"
            placeholder={sections.isPending ? 'Loading sections…' : 'Not linked'}
            data={(sections.data ?? []).map((section) => ({ value: section.key, label: section.title }))}
            value={sectionId}
            disabled={sections.isPending}
            clearable
            onChange={onSectionId}
            renderOption={({ option }) => (
              <Stack gap={0}>
                <Text size="sm">{option.label}</Text>
                <Text size="xs" c="dimmed">
                  {locations.get(option.value) ?? ''}
                </Text>
              </Stack>
            )}
          />

          {sections.error !== null && (
            <Text size="xs" c="red">
              {sections.error.message}
            </Text>
          )}

          <TextInput
            label="Library folder as Plex sees it"
            description={`Where ${rootPath} is mounted inside the Plex server, e.g. /music. Leave empty when Plex sees the same path.`}
            placeholder={placeholder ?? undefined}
            value={libraryPath}
            onChange={(event) => onLibraryPath(event.currentTarget.value)}
          />
        </>
      ) : (
        <Text size="sm" c="dimmed">
          Connect Plex in{' '}
          <Anchor component="button" type="button" onClick={() => void navigate('/settings/plex')}>
            Settings → Plex
          </Anchor>{' '}
          to link this library to a Plex section
        </Text>
      )}
    </Stack>
  );
}

/**
 * One library's form. It is mounted with `key={library.id}`, so a refetch that replaces the library
 * starts the fields from the stored values again rather than keeping edits that never saved.
 */
function LibraryForm({ library }: { library: LibraryResource }) {
  const save = useSaveLibrary();
  const [name, setName] = useState(library.name);
  const [rootPath, setRootPath] = useState(library.rootPath);
  const [layout, setLayout] = useState<LibraryLayoutName>(readEnum<LibraryLayoutName>(library.layout));
  const [namingTemplate, setNamingTemplate] = useState(library.namingTemplate);
  const [albumPolicy, setAlbumPolicy] = useState<AlbumPolicyName>(readEnum<AlbumPolicyName>(library.albumPolicy));
  const [minTracks, setMinTracks] = useState<number | string>(library.minTracksPerRealAlbum);
  const [plexSectionId, setPlexSectionId] = useState<string | null>(library.plexSectionId ?? null);
  const [plexLibraryPath, setPlexLibraryPath] = useState(library.plexLibraryPath ?? '');
  const [outputPolicy, setOutputPolicy] = useState<OutputPolicy>(() => readOutputPolicy(library.outputPolicy));
  const [replayGain, setReplayGain] = useState(library.replayGain ?? false);

  const fields = save.error instanceof ValidationError ? save.error.fields : {};
  const general = Object.entries(fields).filter(([field]) => !KNOWN_FIELDS.has(field));

  const submit = () => {
    // The OpenAPI document types the two enums as integers; the API serialises them as camelCase
    // strings, so the body carries the strings and is asserted once here.
    const body = {
      ...library,
      name,
      rootPath,
      layout,
      namingTemplate,
      albumPolicy,
      minTracksPerRealAlbum: Number(minTracks),
      plexSectionId,
      // An emptied field means "Plex sees the same path", which the API stores as null.
      plexLibraryPath: plexLibraryPath.trim() === '' ? null : plexLibraryPath,
      // The rules as the API reads them: version 2, one rule per source class, every key spelled out.
      outputPolicy: writeOutputPolicy(outputPolicy),
      replayGain,
    } as unknown as LibraryResource;

    save.mutate(body, {
      onSuccess: () => notifications.show({ message: 'Library saved.', color: 'green' }),
    });
  };

  const policyDescriptions = new Map(ALBUM_POLICIES.map((policy) => [policy.value, policy.description]));

  return (
    <Stack gap="md">
      <TextInput
        label="Name"
        value={name}
        error={fields.name}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      <TextInput
        label="Root path"
        value={rootPath}
        error={fields.rootPath}
        onChange={(event) => setRootPath(event.currentTarget.value)}
      />

      <Select
        label="Layout"
        data={LAYOUT_OPTIONS}
        value={layout}
        error={fields.layout}
        allowDeselect={false}
        onChange={(value) => setLayout(readEnum<LibraryLayoutName>(value ?? layout))}
      />

      <NamingTemplateField
        libraryId={Number(library.id)}
        layout={layout}
        value={namingTemplate}
        error={fields.namingTemplate}
        onChange={setNamingTemplate}
      />

      <Select
        label="Album policy"
        data={ALBUM_POLICIES.map((policy) => ({ value: policy.value, label: policy.label }))}
        value={albumPolicy}
        error={fields.albumPolicy}
        allowDeselect={false}
        onChange={(value) => setAlbumPolicy(readEnum<AlbumPolicyName>(value ?? albumPolicy))}
        renderOption={({ option }) => (
          <Stack gap={0}>
            <Text size="sm">{option.label}</Text>
            <Text size="xs" c="dimmed">
              {policyDescriptions.get(readEnum<AlbumPolicyName>(option.value)) ?? ''}
            </Text>
          </Stack>
        )}
      />

      {albumPolicy === 'fewestAlbums' && (
        <NumberInput
          label="Minimum tracks per real album"
          description="A real album is only used when it holds at least this many owned tracks."
          min={1}
          max={10}
          value={minTracks}
          error={fields.minTracksPerRealAlbum}
          onChange={setMinTracks}
        />
      )}

      <PlexBlock
        libraryId={Number(library.id)}
        rootPath={rootPath}
        layout={layout}
        sectionId={plexSectionId}
        libraryPath={plexLibraryPath}
        onSectionId={setPlexSectionId}
        onLibraryPath={setPlexLibraryPath}
      />

      <OutputRulesEditor policy={outputPolicy} onChange={setOutputPolicy} />

      <Switch
        label="Write ReplayGain tags"
        description="Measure each file's loudness (EBU R128, −18 LUFS reference) and write REPLAYGAIN_TRACK_GAIN and REPLAYGAIN_TRACK_PEAK, so players play songs from different sources at one volume. Tags only: the audio is not changed. Files already in the library are measured with “Measure existing files”."
        checked={replayGain}
        onChange={(event) => setReplayGain(event.currentTarget.checked)}
      />

      {general.map(([field, message]) => (
        <Alert key={field} color="red" icon={<CircleAlert size={16} />} title={field}>
          {message}
        </Alert>
      ))}

      {save.error !== null && general.length === 0 && (
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
  );
}

/** The add-library dialog: the three values a library needs, and whether new songs go to it. */
function AddLibraryModal({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (id: number) => void;
}) {
  const create = useCreateLibrary();
  const [name, setName] = useState('');
  const [rootPath, setRootPath] = useState('');
  const [layout, setLayout] = useState<LibraryLayoutName>('artistAlbum');
  const [isDefault, setIsDefault] = useState(false);

  const fields = create.error instanceof ValidationError ? create.error.fields : {};
  const general = Object.entries(fields).filter(([field]) => !['name', 'rootPath', 'layout'].includes(field));

  const close = () => {
    setName('');
    setRootPath('');
    setLayout('artistAlbum');
    setIsDefault(false);
    onClose();
  };

  const submit = () => {
    // A new library carries only what the dialog asks for: the template takes the layout's preset on
    // the server, and an absent policy is the default one (YouTube → AAC 256, the rest kept).
    create.mutate(
      {
        id: 0,
        name,
        rootPath,
        layout,
        namingTemplate: LAYOUT_TEMPLATES[layout],
        sidecarOptions: {},
        albumPolicy: 'fewestAlbums',
        minTracksPerRealAlbum: 2,
        plexSectionId: null,
        plexLibraryPath: null,
        isDefault,
      } as unknown as LibraryResource,
      {
        onSuccess: (created) => {
          notifications.show({ message: 'Library created.', color: 'green' });
          onCreated(Number(created.id));
          close();
        },
      },
    );
  };

  return (
    <Modal opened={opened} onClose={close} title="Add library">
      <Stack gap="md">
        <TextInput
          label="Name"
          value={name}
          error={fields.name}
          onChange={(event) => setName(event.currentTarget.value)}
        />

        <TextInput
          label="Root path"
          value={rootPath}
          error={fields.rootPath}
          onChange={(event) => setRootPath(event.currentTarget.value)}
        />

        <Select
          label="Layout"
          data={LAYOUT_OPTIONS}
          value={layout}
          error={fields.layout}
          allowDeselect={false}
          onChange={(value) => setLayout(readEnum<LibraryLayoutName>(value ?? layout))}
        />

        <Checkbox
          label="Make this the default library"
          description="New songs are filed here when nothing else chooses."
          checked={isDefault}
          onChange={(event) => setIsDefault(event.currentTarget.checked)}
        />

        {general.map(([field, message]) => (
          <Alert key={field} color="red" icon={<CircleAlert size={16} />} title={field}>
            {message}
          </Alert>
        ))}

        {create.error !== null && general.length === 0 && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {create.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            Cancel
          </Button>
          <Button loading={create.isPending} onClick={submit}>
            Add library
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The delete-library dialog: one confirmation, and the reason the API refuses when it does. */
function DeleteLibraryModal({
  library,
  opened,
  onClose,
}: {
  library: LibraryResource;
  opened: boolean;
  onClose: () => void;
}) {
  const remove = useDeleteLibrary();

  return (
    <Modal opened={opened} onClose={onClose} title={`Delete ${library.name}`}>
      <Stack gap="md">
        <Text size="sm">
          Delete the library {library.name} ({library.rootPath})? Files on disk are not touched.
        </Text>

        {remove.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {remove.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button
            color="red"
            loading={remove.isPending}
            onClick={() => remove.mutate(Number(library.id), { onSuccess: onClose })}
          >
            Delete library
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The library settings: one panel per library, each with its form, rules and commands. */
export function LibrarySettingsPage() {
  const libraries = useLibraries();
  const [adding, setAdding] = useState(false);
  const [compacting, setCompacting] = useState<LibraryResource | null>(null);
  const [converting, setConverting] = useState<LibraryResource | null>(null);
  const runCommand = useRunCommand();

  // ApplyReplayGain measures and tags the files of a library that has the switch on (LIBRARY_OUTPUT §7.8).
  const measure = (library: LibraryResource) => {
    const command: CommandRequest & { libraryId: number } = { name: 'ApplyReplayGain', libraryId: Number(library.id) };

    runCommand.mutate(command, {
      onSuccess: () =>
        notifications.show({ message: `Measuring the files of ${library.name}: see System → Tasks.`, color: 'green' }),
      onError: (error) => notifications.show({ message: error.message, color: 'red' }),
    });
  };
  const [deleting, setDeleting] = useState<LibraryResource | null>(null);
  const [active, setActive] = useState<string | null>(null);

  if (libraries.isPending) {
    return <LoadingState />;
  }

  if (libraries.error !== null) {
    return <ErrorState message={libraries.error.message} />;
  }

  const rows = libraries.data ?? [];

  if (rows.length === 0) {
    return <EmptyState message="No library is configured." />;
  }

  // The first library is shown until the user picks one; a created library is shown at once.
  const selected = active ?? String(rows[0].id);

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Group justify="space-between">
          <Text fw={600}>Libraries</Text>
          <Button variant="light" onClick={() => setAdding(true)}>
            Add library…
          </Button>
        </Group>

        <Tabs value={selected} onChange={setActive}>
          <Tabs.List>
            {rows.map((library) => (
              <Tabs.Tab key={String(library.id)} value={String(library.id)}>
                {library.name}
                {library.isDefault && (
                  <Badge variant="light" ml="xs">
                    default
                  </Badge>
                )}
              </Tabs.Tab>
            ))}
          </Tabs.List>

          {rows.map((library) => (
            <Tabs.Panel key={String(library.id)} value={String(library.id)} pt="md">
              <Stack gap="md">
                <Group justify="space-between">
                  <Group gap="xs">
                    <Text fw={600}>{library.isDefault ? 'Default library' : 'Library'}</Text>
                  </Group>

                  <Group gap="xs">
                    {library.replayGain === true && (
                      <Button variant="light" loading={runCommand.isPending} onClick={() => measure(library)}>
                        Measure existing files
                      </Button>
                    )}
                    <Button variant="light" onClick={() => setConverting(library)}>
                      Convert existing files…
                    </Button>
                    <Button variant="light" onClick={() => setCompacting(library)}>
                      Compact library…
                    </Button>
                    {!library.isDefault && (
                      <Button variant="light" color="red" onClick={() => setDeleting(library)}>
                        Delete library…
                      </Button>
                    )}
                  </Group>
                </Group>

                <LibraryForm key={String(library.id)} library={library} />
              </Stack>
            </Tabs.Panel>
          ))}
        </Tabs>
      </Stack>

      <AddLibraryModal opened={adding} onClose={() => setAdding(false)} onCreated={(id) => setActive(String(id))} />

      <CompactLibraryModal
        libraryId={Number(compacting?.id ?? 0)}
        libraryName={compacting?.name ?? ''}
        opened={compacting !== null}
        onClose={() => setCompacting(null)}
      />

      <ConvertLibraryModal
        libraryId={Number(converting?.id ?? 0)}
        libraryName={converting?.name ?? ''}
        opened={converting !== null}
        onClose={() => setConverting(null)}
      />

      {deleting !== null && <DeleteLibraryModal library={deleting} opened onClose={() => setDeleting(null)} />}
    </Card>
  );
}
