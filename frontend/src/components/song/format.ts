/** Formatting the song page shares between its tabs. Pure functions, no React. */

type Num = number | string | null | undefined;

const toNumber = (value: Num): number | null => {
  if (value === null || value === undefined || value === '') {
    return null;
  }

  const parsed = Number(value);

  return Number.isFinite(parsed) ? parsed : null;
};

/** `12.3 MB`; an em dash when nothing usable arrived. */
export function formatBytes(bytes: Num): string {
  const value = toNumber(bytes);

  if (value === null || value < 0) {
    return '—';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let scaled = value;
  let unit = 0;

  while (scaled >= 1024 && unit < units.length - 1) {
    scaled /= 1024;
    unit += 1;
  }

  const rounded = unit === 0 || scaled >= 10 ? Math.round(scaled) : Number(scaled.toFixed(1));

  return `${rounded} ${units[unit]}`;
}

/** `44.1 kHz` from a rate in hertz. */
export function formatSampleRate(hertz: Num): string {
  const value = toNumber(hertz);

  return value === null ? '—' : `${Number((value / 1000).toFixed(1))} kHz`;
}

/** `Stereo`, `Mono` or `6 channels`. */
export function formatChannels(channels: Num): string {
  const value = toNumber(channels);

  if (value === null) {
    return '—';
  }

  return value === 2 ? 'Stereo' : value === 1 ? 'Mono' : `${value} channels`;
}

/** A decibel figure with its sign: `-6.3 dB`. */
export function formatDecibels(db: Num): string {
  const value = toNumber(db);

  return value === null ? '—' : `${Number(value.toFixed(2))} dB`;
}

/** A whole number with the reader's thousands separators. */
export function formatCount(count: Num): string {
  const value = toNumber(count);

  return value === null ? '—' : value.toLocaleString();
}

/** A local date and time, or the input back when it does not parse. */
export function formatDateTime(iso: string | null | undefined): string {
  if (iso === null || iso === undefined || iso === '') {
    return '—';
  }

  const parsed = new Date(iso);

  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleString();
}

const ENTITIES: Record<string, string> = {
  '&amp;': '&',
  '&lt;': '<',
  '&gt;': '>',
  '&quot;': '"',
  '&#39;': "'",
  '&apos;': "'",
  '&nbsp;': ' ',
};

/**
 * Text from an outside source (a Last.fm wiki or bio) as plain text: tags are dropped and the common
 * entities decoded. The result is only ever rendered as a React text child, never as markup.
 */
export function plainText(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return '';
  }

  return value
    .replace(/<[^>]*>/g, '')
    .replace(/&(?:amp|lt|gt|quot|apos|nbsp|#39);/g, (entity) => ENTITIES[entity] ?? entity)
    .trim();
}

/** The URL when it is an http(s) one, else `null`: an outside link is never rendered as anything else. */
export function safeUrl(url: string | null | undefined): string | null {
  if (url === null || url === undefined) {
    return null;
  }

  return /^https?:\/\//i.test(url) ? url : null;
}

/** Synced lyrics as plain lines: the `[mm:ss.xx]` time tags and the `[ar:…]` header lines are dropped. */
export function lyricLines(synced: string): string[] {
  return synced
    .split(/\r?\n/)
    .filter((line) => !/^\s*\[[a-z]+:[^\]]*\]\s*$/i.test(line))
    .map((line) => line.replace(/\[\d+:\d+(?:[.:]\d+)?\]/g, '').trim());
}
