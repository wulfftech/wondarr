import { Select } from '@mantine/core';
import type { LibraryChoice } from '../api/libraryChoice';

/** The library a song is filed into; hidden while there is only one library to choose. */
export function LibraryPicker({ choice }: { choice: LibraryChoice }) {
  if (!choice.visible) {
    return null;
  }

  return (
    <Select
      label="Library"
      data={choice.options}
      value={choice.value}
      allowDeselect={false}
      w={220}
      onChange={choice.pick}
    />
  );
}
