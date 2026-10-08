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
