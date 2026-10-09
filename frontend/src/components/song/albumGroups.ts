import type { AlbumOptionView } from './AlbumOptionLabel';

/** What grouping needs of an option: its identity, its MusicBrainz release group, and whether it is the song's album. */
export interface GroupableOption extends AlbumOptionView {
  key: string;
  mbReleaseGroupId: string | null;
  isCurrent: boolean;
}

/** Every edition of one album (one MusicBrainz release group), or a lone option with no group id. */
export interface AlbumGroup<T extends GroupableOption> {
  /** The release group id, or the option's own key when it has none. */
  id: string;
  editions: T[];
  hasCurrent: boolean;
  /** The group as one row: the first edition's title, artist and types, the earliest year, the first cover found. */
  view: AlbumOptionView;
}

/** The earliest date of the editions (ISO dates sort as text), or `null` when none has one. */
function earliestDate(editions: AlbumOptionView[]): string | null {
  const dates = editions.map((edition) => edition.date).filter((date): date is string => date != null && date !== '');

  return dates.length === 0 ? null : dates.reduce((a, b) => (b < a ? b : a));
}

/**
 * Groups options by `mbReleaseGroupId`, in the order each group first appears; an option without a group
 * id is a group of its own. A one-edition group shows as that edition, unchanged.
 */
export function groupAlbumOptions<T extends GroupableOption>(options: T[]): AlbumGroup<T>[] {
  const groups = new Map<string, T[]>();

  for (const option of options) {
    const id = option.mbReleaseGroupId != null && option.mbReleaseGroupId !== '' ? option.mbReleaseGroupId : option.key;
    const bucket = groups.get(id);

    if (bucket === undefined) {
      groups.set(id, [option]);
    } else {
      bucket.push(option);
    }
  }

  return [...groups.entries()].map(([id, editions]) => {
    const first = editions[0];

    if (editions.length === 1) {
      return { id, editions, hasCurrent: first.isCurrent, view: first };
    }

    return {
      id,
      editions,
      hasCurrent: editions.some((edition) => edition.isCurrent),
      view: {
        ...first,
        date: earliestDate(editions),
        trackNo: null,
        totalTracks: null,
        discNo: null,
        coverUrl: editions.find((edition) => edition.coverUrl != null && edition.coverUrl !== '')?.coverUrl ?? null,
        editions: editions.length,
      },
    };
  });
}

/** Whether a group matches the filter box: its title or album artist contains the needle (lower case). */
export function groupMatches<T extends GroupableOption>(group: AlbumGroup<T>, needle: string): boolean {
  return group.editions.some(
    (edition) => edition.title.toLowerCase().includes(needle) || edition.albumArtist.toLowerCase().includes(needle),
  );
}

/** One edition inside an open group: `Track 3 of 22 · 2003 · Bootleg · US` (no cover; the group row has it). */
export function editionText(option: AlbumOptionView): string {
  const parts: string[] = [];

  if (option.trackNo != null && option.totalTracks != null) {
    parts.push(`Track ${option.trackNo} of ${option.totalTracks}`);
  } else if (option.totalTracks != null) {
    parts.push(`${option.totalTracks} tracks`);
  }

  if (option.date != null && option.date !== '') {
    parts.push(option.date.slice(0, 4));
  }

  if (option.status != null && option.status !== '') {
    parts.push(option.status);
  }

  if (option.country != null && option.country !== '') {
    parts.push(option.country);
  }

  return parts.length > 0 ? parts.join(' · ') : 'Edition';
}
