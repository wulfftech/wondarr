import { useState } from 'react';
import { useLibraries } from './profiles';

/** What the library picker of the add screens is, and the library it sends when the user has not picked. */
export interface LibraryChoice {
  /** Whether the picker is shown at all: one library needs no choosing. */
  visible: boolean;
  options: { value: string; label: string }[];
  /** The id the add requests carry: the picked library, else the default one. */
  value: string | null;
  pick: (id: string | null) => void;
}

/** The libraries songs can be filed into, with the default one as the picker's starting value. */
export function useLibraryChoice(): LibraryChoice {
  const libraries = useLibraries();
  const rows = libraries.data ?? [];
  const [picked, setPicked] = useState<string | null>(null);
  const fallback = rows.find((library) => library.isDefault) ?? rows[0];

  return {
    visible: rows.length > 1,
    options: rows.map((library) => ({ value: String(library.id), label: library.name })),
    value: picked ?? (fallback === undefined ? null : String(fallback.id)),
    pick: setPicked,
  };
}
