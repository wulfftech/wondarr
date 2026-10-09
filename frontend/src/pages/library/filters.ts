import type { CustomFilterEntry } from '../../api/customFilters';
import type { SongFilters } from '../../api/songs';

/** The Library page's filters as the controls hold them, and how they map to the API and to saved views. */

export type MonitoredFilter = 'all' | 'monitored' | 'unmonitored';
export type FileFilter = 'any' | 'has' | 'missing';
export type CutoffFilter = 'any' | 'met' | 'unmet';

export interface LibraryFilters {
  term: string;
  artistId: string | null;
  monitored: MonitoredFilter;
  libraryId: string | null;
  qualityProfileId: string | null;
  file: FileFilter;
  cutoff: CutoffFilter;
  tag: string | null;
}

/** Nothing filtered. */
export const NO_FILTERS: LibraryFilters = {
  term: '',
  artistId: null,
  monitored: 'all',
  libraryId: null,
  qualityProfileId: null,
  file: 'any',
  cutoff: 'any',
  tag: null,
};

/** True when no filter is set. */
export function isUnfiltered(filters: LibraryFilters): boolean {
  return toViewEntries(filters).length === 0;
}

/** The query `GET /api/v1/song` takes. */
export function toSongFilters(filters: LibraryFilters): SongFilters {
  return {
    term: filters.term === '' ? undefined : filters.term,
    artistId: filters.artistId === null ? undefined : Number(filters.artistId),
    monitored: filters.monitored === 'all' ? undefined : filters.monitored === 'monitored',
    libraryId: filters.libraryId === null ? undefined : Number(filters.libraryId),
    qualityProfileId: filters.qualityProfileId === null ? undefined : Number(filters.qualityProfileId),
    hasFile: filters.file === 'any' ? undefined : filters.file === 'has',
    cutoffMet: filters.cutoff === 'any' ? undefined : filters.cutoff === 'met',
    tag: filters.tag === null ? undefined : filters.tag,
  };
}

/** A saved view's entries: one per set filter, keyed by the API's own query parameter, valued as a string. */
export function toViewEntries(filters: LibraryFilters): CustomFilterEntry[] {
  const query = toSongFilters(filters);
  const entries: CustomFilterEntry[] = [];

  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined) {
      entries.push({ key, value: String(value), type: 'equal' });
    }
  }

  return entries;
}

/** The filters a saved view stands for; entries this page does not know are ignored. */
export function fromViewEntries(entries: { key: string; value: string }[]): LibraryFilters {
  const filters: LibraryFilters = { ...NO_FILTERS };

  for (const { key, value } of entries) {
    switch (key) {
      case 'term':
        filters.term = value;
        break;
      case 'artistId':
        filters.artistId = value;
        break;
      case 'monitored':
        filters.monitored = value === 'true' ? 'monitored' : 'unmonitored';
        break;
      case 'libraryId':
        filters.libraryId = value;
        break;
      case 'qualityProfileId':
        filters.qualityProfileId = value;
        break;
      case 'hasFile':
        filters.file = value === 'true' ? 'has' : 'missing';
        break;
      case 'cutoffMet':
        filters.cutoff = value === 'true' ? 'met' : 'unmet';
        break;
      case 'tag':
        filters.tag = value;
        break;
    }
  }

  return filters;
}
