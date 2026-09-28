import { describe, expect, it } from 'vitest';
import html from '../../index.html?raw';

// The API host replaces every occurrence of the URL-base placeholder in index.html (P0-08a). A
// second literal copy (for example in the dev-server fallback script) would be rewritten too and
// could reset the real <base> to "/", which breaks every asset URL under a URL base.
describe('index.html', () => {
  it('contains the URL-base placeholder only in the base tag', () => {
    const placeholder = '__URL' + '_BASE__';

    expect(html.split(placeholder).length - 1).toBe(1);
    expect(html).toContain(`<base href="${placeholder}/" />`);
  });
});
