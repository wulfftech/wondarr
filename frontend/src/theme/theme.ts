import { createTheme } from '@mantine/core';

/**
 * The Mantine theme. Layout only uses CSS modules on top of this; there is no other ad-hoc CSS.
 */
export const theme = createTheme({
  primaryColor: 'indigo',
  fontFamily: 'Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
  fontFamilyMonospace: 'ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace',
  defaultRadius: 'md',
});
