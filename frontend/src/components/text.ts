import type { UpdateStatus } from '../api/update';

/**
 * A value the API sends as a word, a camelCase or a snake_case name, as it reads on screen: the
 * first letter capitalised and a name split into words (`importFailed` and `import_failed` →
 * "Import failed"). A value already in capitals, such as `EP`, keeps them.
 */
export function initCaps(value: string): string {
  const spaced = value.replace(/_/g, ' ');
  const words = /[a-z][A-Z]/.test(spaced) ? spaced.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase() : spaced;

  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** The address to link to, or `null` when it is not an http(s) address. */
export function safeHttpUrl(value: string): string | null {
  try {
    const url = new URL(value.trim());

    return url.protocol === 'http:' || url.protocol === 'https:' ? url.toString() : null;
  } catch {
    return null;
  }
}

/** How long ago, in words ("5 minutes ago"); `now` is a parameter so a test can pin it. */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (iso === null || iso === undefined || iso === '') {
    return 'never';
  }

  const then = new Date(iso).getTime();

  if (Number.isNaN(then)) {
    return 'never';
  }

  const seconds = Math.max(0, Math.round((now - then) / 1000));

  if (seconds < 45) {
    return 'just now';
  }

  const plural = (count: number, unit: string) => `${count} ${unit}${count === 1 ? '' : 's'} ago`;

  if (seconds < 3600) {
    return plural(Math.max(1, Math.round(seconds / 60)), 'minute');
  }

  if (seconds < 86400) {
    return plural(Math.round(seconds / 3600), 'hour');
  }

  return plural(Math.round(seconds / 86400), 'day');
}

/** What the Status page's "Update" row says. */
export function updateSummary(status: UpdateStatus): string {
  if (!status.checkEnabled) {
    return 'Off';
  }

  if (status.isDevelopmentBuild) {
    return 'Development build';
  }

  if (status.updateAvailable && status.latestVersion !== null) {
    return `${status.latestVersion} available`;
  }

  return status.latestVersion === null ? 'Not checked yet' : 'Up to date';
}
