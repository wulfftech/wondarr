import { ActionIcon, Group, Stack } from '@mantine/core';
import { ChevronDown, ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { AlbumOptionLabel, type AlbumOptionView } from './AlbumOptionLabel';

/**
 * A group of editions as one row: a chevron, the group's label (with "N editions") and, when open, the
 * editions underneath, indented. The caller supplies the edition rows, so the picker can make them radios.
 */
export function AlbumGroupRow({
  view,
  open,
  onToggle,
  badge,
  children,
}: {
  view: AlbumOptionView;
  open: boolean;
  onToggle: () => void;
  badge?: ReactNode;
  children: ReactNode;
}) {
  return (
    <Stack gap="xs">
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <ActionIcon
          variant="subtle"
          color="gray"
          size="sm"
          aria-expanded={open}
          aria-label={`${open ? 'Hide' : 'Show'} the editions of ${view.title}`}
          onClick={onToggle}
        >
          {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        </ActionIcon>
        <AlbumOptionLabel option={view} />
        {badge}
      </Group>
      {open && (
        <Stack gap="sm" pl={32} role="group" aria-label={`Editions of ${view.title}`}>
          {children}
        </Stack>
      )}
    </Stack>
  );
}
