import {
  Alert,
  Anchor,
  Button,
  Card,
  Group,
  NumberInput,
  Popover,
  Select,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { CircleAlert } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import {
  ALBUM_POLICIES,
  LIBRARY_LAYOUTS,
  readEnum,
  useLibraries,
  useNamingPreview,
  useSaveLibrary,
  ValidationError,
  type AlbumPolicyName,
  type LibraryLayoutName,
  type LibraryResource,
} from '../../api/profiles';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

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

/** The library settings: the default library's layout, naming template and album policy. */
export function LibrarySettingsPage() {
  const libraries = useLibraries();

  if (libraries.isPending) {
    return <LoadingState />;
  }

  if (libraries.error !== null) {
    return <ErrorState message={libraries.error.message} />;
  }

  const rows = libraries.data ?? [];
  const library = rows.find((candidate) => candidate.isDefault) ?? rows[0];

  if (library === undefined) {
    return <EmptyState message="No library is configured." />;
  }

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Text fw={600}>{library.isDefault ? 'Default library' : 'Library'}</Text>
        <LibraryForm key={String(library.id)} library={library} />
      </Stack>
    </Card>
  );
}
