import { describe, expect, it } from 'vitest';
import type { NotificationFieldResource } from '../api/notifications';
import { targetOf } from './SchemaFields';

/** The Email provider's address fields, as `GET /api/v1/notification/schema` describes them. */
const EMAIL_TARGET_FIELDS: NotificationFieldResource[] = [
  {
    name: 'host',
    label: 'SMTP host',
    type: 'text',
    required: true,
    helpText: null,
    options: null,
    secret: false,
    advanced: false,
    target: true,
  },
  {
    name: 'port',
    label: 'Port',
    type: 'number',
    required: false,
    helpText: null,
    options: null,
    secret: false,
    advanced: false,
    target: true,
    targetDefault: '587',
  },
  {
    name: 'password',
    label: 'Password',
    type: 'password',
    required: false,
    helpText: null,
    options: null,
    secret: true,
    advanced: false,
    target: false,
  },
];

describe('targetOf', () => {
  it('treats a blank value of a field that has a default as that default', () => {
    const blank = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: '' });
    const spaces = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: '  ' });
    const unset = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com' });
    const typed = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: 587 });
    const typedText = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: '587' });

    expect(blank).toBe(typed);
    expect(spaces).toBe(typed);
    expect(unset).toBe(typed);
    expect(typedText).toBe(typed);
  });

  it('still tells another port, another host and a blank field without a default apart', () => {
    const base = targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: '' });

    expect(targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.example.com', port: 465 })).not.toBe(base);
    expect(targetOf(EMAIL_TARGET_FIELDS, { host: 'smtp.other.example', port: '' })).not.toBe(base);
    expect(targetOf(EMAIL_TARGET_FIELDS, { host: '', port: '' })).not.toBe(base);
  });
});
