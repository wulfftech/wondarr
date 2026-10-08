import { describe, expect, it } from 'vitest';
import { initCaps } from './text';

describe('initCaps', () => {
  it('capitalises a single word', () => {
    expect(initCaps('live')).toBe('Live');
  });

  it('splits a camelCase name into words', () => {
    expect(initCaps('importFailed')).toBe('Import failed');
  });

  it('splits a snake_case name into words', () => {
    expect(initCaps('radio_edit')).toBe('Radio edit');
  });

  it('keeps a value already in capitals', () => {
    expect(initCaps('EP')).toBe('EP');
  });

  it('leaves an empty value empty', () => {
    expect(initCaps('')).toBe('');
  });
});
