import { Alert, Anchor, Breadcrumbs, Button, Group, Loader, Modal, Stack, Text, TextInput } from '@mantine/core';
import { CircleAlert, Folder, FolderPlus } from 'lucide-react';
import { useState } from 'react';
import { useCreateFolder, useFolderListing } from '../api/filesystem';
import { ValidationError } from '../api/profiles';

interface FolderPickerProps {
  opened: boolean;
  onClose: () => void;
  /** Called with the folder the person selected; the picker closes itself afterwards. */
  onSelect: (path: string) => void;
  /** Where to open, when that is a folder the picker may enter. */
  initialPath?: string | null;
}

/** One step of the breadcrumb: its label and the folder it goes to (null for the list of roots). */
interface Crumb {
  label: string;
  path: string | null;
}

/** The separator a path uses: a Windows path (`C:\\Videos`) has backslashes and no slash. */
function separatorOf(path: string): string {
  return path.includes('\\') && !path.includes('/') ? '\\' : '/';
}

/** The root that holds `path`, the longest one when mounts are nested. */
function rootOf(path: string, roots: string[]): string | undefined {
  const separator = separatorOf(path);
  const same = (a: string, b: string) => (separator === '\\' ? a.toLowerCase() === b.toLowerCase() : a === b);

  return roots
    .filter((root) => {
      const prefix = root.endsWith(separator) ? root : `${root}${separator}`;

      return same(path, root) || same(path.slice(0, prefix.length), prefix);
    })
    .sort((a, b) => b.length - a.length)[0];
}

/** "Mounted folders", the root, then one step per folder below it. */
function crumbsFor(path: string | null, roots: string[]): Crumb[] {
  const crumbs: Crumb[] = [{ label: 'Mounted folders', path: null }];

  if (path === null) {
    return crumbs;
  }

  const separator = separatorOf(path);
  const root = rootOf(path, roots) ?? path;
  crumbs.push({ label: root, path: root });

  let walked = root.endsWith(separator) ? root.slice(0, -1) : root;

  for (const segment of path
    .slice(root.length)
    .split(separator)
    .filter((part) => part !== '')) {
    walked = `${walked}${separator}${segment}`;
    crumbs.push({ label: segment, path: walked });
  }

  return crumbs;
}

/**
 * A modal for choosing a folder. It lists only the places the server offers (in a container, the mounted
 * volumes), so a folder chosen here is always one that survives the container being recreated.
 */
export function FolderPicker({ opened, onClose, onSelect, initialPath = null }: FolderPickerProps) {
  const [current, setCurrent] = useState<string | null>(initialPath);
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState('');
  const listing = useFolderListing(current, opened);
  const create = useCreateFolder();

  const roots = listing.data?.roots ?? [];
  const crumbs = crumbsFor(
    listing.data?.path ?? null,
    roots.map((root) => root.path),
  );
  const nameError =
    create.error instanceof ValidationError
      ? (create.error.fields.name ?? create.error.message)
      : create.error?.message;

  const goTo = (path: string | null) => {
    setCurrent(path);
    setNaming(false);
    setName('');
    create.reset();
  };

  const makeFolder = () => {
    const parent = listing.data?.path;

    if (!parent) {
      return;
    }

    create.mutate({ parent, name }, { onSuccess: (folder) => goTo(folder.path) });
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Choose a folder" size="lg">
      <Stack gap="sm">
        <Breadcrumbs>
          {crumbs.map((crumb, index) =>
            index === crumbs.length - 1 ? (
              <Text key={crumb.label + String(crumb.path)} size="sm" fw={600}>
                {crumb.label}
              </Text>
            ) : (
              <Anchor
                key={crumb.label + String(crumb.path)}
                component="button"
                type="button"
                size="sm"
                onClick={() => goTo(crumb.path)}
              >
                {crumb.label}
              </Anchor>
            ),
          )}
        </Breadcrumbs>

        {listing.isPending && <Loader size="sm" />}

        {listing.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            <Stack gap="xs" align="flex-start">
              <Text size="sm">{listing.error.message}</Text>
              {current !== null && (
                <Button size="xs" variant="default" onClick={() => goTo(null)}>
                  Show mounted folders
                </Button>
              )}
            </Stack>
          </Alert>
        )}

        {listing.data !== undefined && listing.data.path === null && listing.data.roots.length === 0 && (
          <Text size="sm" c="dimmed">
            No folder is mounted into this container. Mount a folder or a share, then reopen this window.
          </Text>
        )}

        {listing.data?.path === null &&
          listing.data.roots.map((root) => (
            <Button
              key={root.path}
              variant="default"
              justify="flex-start"
              disabled={root.unavailable}
              leftSection={<Folder size={16} />}
              onClick={() => goTo(root.path)}
            >
              {root.unavailable ? `${root.path} (not responding)` : root.path}
            </Button>
          ))}

        {listing.data?.path !== null &&
          listing.data?.folders.map((folder) => (
            <Button
              key={folder.path}
              variant="subtle"
              justify="flex-start"
              leftSection={<Folder size={16} />}
              onClick={() => goTo(folder.path)}
            >
              {folder.name}
            </Button>
          ))}

        {listing.data?.path !== null && listing.data?.folders.length === 0 && (
          <Text size="sm" c="dimmed">
            No folders in here.
          </Text>
        )}

        {naming && (
          <Group align="flex-start" gap="xs">
            <TextInput
              aria-label="New folder name"
              placeholder="Folder name"
              value={name}
              error={nameError}
              style={{ flex: 1 }}
              onChange={(event) => setName(event.currentTarget.value)}
            />
            <Button loading={create.isPending} disabled={name.trim() === ''} onClick={makeFolder}>
              Create
            </Button>
          </Group>
        )}

        <Group justify="space-between">
          {listing.data?.path !== null && listing.data !== undefined ? (
            <Button
              variant="default"
              leftSection={<FolderPlus size={16} />}
              onClick={() => {
                setNaming(true);
                create.reset();
              }}
            >
              New folder…
            </Button>
          ) : (
            <span />
          )}

          <Group gap="xs">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button
              disabled={listing.data?.path === null || listing.data === undefined}
              onClick={() => {
                if (listing.data?.path) {
                  onSelect(listing.data.path);
                  onClose();
                }
              }}
            >
              Select this folder
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
