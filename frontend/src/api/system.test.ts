import { describe, expect, it } from 'vitest';
import { createTestConfig } from '../test/helpers';
import { backupDownloadTarget, downloadUrl, logFileDownloadTarget } from './system';

describe('downloadUrl', () => {
  it('carries the token and never the API key', () => {
    const config = createTestConfig();
    const target = backupDownloadTarget('wondarr backup.zip');

    const url = downloadUrl(config, target.path, 'abc.def');

    expect(url).toContain('/api/v1/system/backup/wondarr%20backup.zip/download?token=abc.def');
    expect(url).not.toContain('apikey');
    expect(url).not.toContain(config.apiKey);
  });

  it('goes through the URL base', () => {
    const url = downloadUrl(createTestConfig({ urlBase: '/wondarr' }), logFileDownloadTarget('a.json').path, 't');

    expect(url).toContain('/wondarr/api/v1/log/file/a.json?token=t');
  });
});
