import { Alert, Button, Group, Menu, Modal, Stack, Text, TextInput } from '@mantine/core';
import { Bookmark, ChevronDown, CircleAlert } from 'lucide-react';
import { useState } from 'react';
import {
  useCreateCustomFilter,
  useDeleteCustomFilter,
  type CustomFilterEntry,
  type CustomFilterResource,
} from '../../api/customFilters';

/** The Library page's saved views: pick one, save the current filters as one, delete the active one. */

export const LIBRARY_VIEW_TYPE = 'library';

interface SavedViewsMenuProps {
  views: CustomFilterResource[];
  active: CustomFilterResource | null;
  /** The filters as they are now, one entry per set filter. */
  current: CustomFilterEntry[];
  onApply: (view: CustomFilterResource) => void;
  /** Called with the view just saved, so the page can mark it active. */
  onSaved: (view: CustomFilterResource) => void;
  /** Called after the active view is deleted. */
  onDeleted: () => void;
}

export function SavedViewsMenu({ views, active, current, onApply, onSaved, onDeleted }: SavedViewsMenuProps) {
  const [dialog, setDialog] = useState<'save' | 'delete' | null>(null);
  const [label, setLabel] = useState('');
  const create = useCreateCustomFilter();
  const remove = useDeleteCustomFilter();

  const close = () => {
    setDialog(null);
    create.reset();
    remove.reset();
  };

  const save = () => {
    create.mutate(
      { type: LIBRARY_VIEW_TYPE, label: label.trim(), filters: current },
      {
        onSuccess: (view) => {
          onSaved(view);
          close();
        },
      },
    );
  };

  return (
    <>
      <Menu withinPortal>
        <Menu.Target>
          <Button variant="default" leftSection={<Bookmark size={16} />} rightSection={<ChevronDown size={14} />}>
            {active?.label ?? 'Views'}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          {views.length === 0 && <Menu.Label>No saved views yet</Menu.Label>}
          {views.map((view) => (
            <Menu.Item key={String(view.id)} onClick={() => onApply(view)}>
              {view.label}
            </Menu.Item>
          ))}
          <Menu.Divider />
          <Menu.Item
            disabled={current.length === 0}
            onClick={() => {
              setLabel('');
              setDialog('save');
            }}
          >
            Save view…
          </Menu.Item>
          {active !== null && (
            <Menu.Item color="red" onClick={() => setDialog('delete')}>
              Delete view
            </Menu.Item>
          )}
        </Menu.Dropdown>
      </Menu>

      <Modal opened={dialog === 'save'} onClose={close} title="Save view">
        <Stack gap="md">
          <TextInput
            label="Name"
            placeholder="Missing files"
            maxLength={100}
            data-autofocus
            value={label}
            onChange={(event) => setLabel(event.currentTarget.value)}
          />

          {create.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {create.error.message}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button disabled={label.trim() === ''} loading={create.isPending} onClick={save}>
              Save
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={dialog === 'delete'} onClose={close} title="Delete view">
        <Stack gap="md">
          <Text size="sm">Delete the saved view “{active?.label ?? ''}”? The songs are not touched.</Text>

          {remove.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {remove.error.message}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={() => {
                if (active !== null) {
                  remove.mutate(Number(active.id), {
                    onSuccess: () => {
                      onDeleted();
                      close();
                    },
                  });
                }
              }}
            >
              Delete
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
