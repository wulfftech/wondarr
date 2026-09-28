import { Alert, Button, Card, Group, NumberInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import {
  ALBUM_POLICIES,
  LIBRARY_LAYOUTS,
  readEnum,
  useLibraries,
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

/** The template tokens the hint lists, in the order a path is built from them. */
const TEMPLATE_TOKENS = ['{Artist Name}', '{Album Title}', '{Track Title}', '{track:00}'];

const LAYOUT_OPTIONS = LIBRARY_LAYOUTS.map((layout) => ({ value: layout.value, label: layout.label }));

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

      <TextInput
        label="Naming template"
        description={`Tokens: ${TEMPLATE_TOKENS.join(', ')}`}
        value={namingTemplate}
        error={fields.namingTemplate}
        onChange={(event) => setNamingTemplate(event.currentTarget.value)}
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
