import {
  ActionIcon,
  Button,
  Checkbox,
  Group,
  NumberInput,
  PasswordInput,
  Select,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { Trash2 } from 'lucide-react';
import {
  SECRET_MASK,
  type NotificationFieldResource,
  type NotificationKeyValue,
  type NotificationSettings,
} from '../api/notifications';

/**
 * The form a provider's schema renders (ARCHITECTURE §5.7): one input per field, typed the way the
 * server's `type` names it. The notifications endpoints and the import lists share the field shape,
 * so both settings pages render their provider's form with these helpers.
 */

/** The stored rows of a `keyValueList` field, in the order the provider keeps them. */
function asPairs(value: unknown): NotificationKeyValue[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.map((entry) => {
    const row = typeof entry === 'object' && entry !== null ? (entry as Record<string, unknown>) : {};

    return {
      key: typeof row.key === 'string' ? row.key : '',
      value: typeof row.value === 'string' ? row.value : '',
    };
  });
}

/** The value a field starts with when the notification has never stored one. */
// Shared with the pages that render a provider's form, so the fast-refresh export rule does not apply.
// eslint-disable-next-line react-refresh/only-export-components
export function emptyValue(field: NotificationFieldResource): unknown {
  switch (field.type) {
    case 'checkbox':
      return false;
    case 'keyValueList':
      return [];
    case 'select':
      return field.options?.[0] ?? '';
    default:
      return '';
  }
}

/**
 * The form's values, read off the schema and the stored settings. A secret that reads back as the
 * mask starts empty — its placeholder carries the mask — so an untouched field sends the mask back
 * and the server keeps what it stored.
 */
// eslint-disable-next-line react-refresh/only-export-components
export function initialValues(
  fields: NotificationFieldResource[],
  stored: NotificationSettings,
): { values: Record<string, unknown>; masked: Set<string> } {
  const values: Record<string, unknown> = {};
  const masked = new Set<string>();

  for (const field of fields) {
    const value = stored[field.name];

    if (field.secret && value === SECRET_MASK) {
      values[field.name] = '';
      masked.add(field.name);
      continue;
    }

    if (field.type === 'keyValueList') {
      values[field.name] = asPairs(value);
      continue;
    }

    values[field.name] = value ?? emptyValue(field);
  }

  return { values, masked };
}

/** A URL as scheme, host and port, lower-cased, so two spellings of one server compare equal. */
// Shared with the Soulseek connection card, which compares one address, so the fast-refresh export rule does not apply.
// eslint-disable-next-line react-refresh/only-export-components
export function urlTarget(raw: string): string {
  const text = raw.trim();

  try {
    const url = new URL(text);

    return `${url.protocol}//${url.hostname}:${url.port === '' ? (url.protocol === 'https:' ? '443' : '80') : url.port}`.toLowerCase();
  } catch {
    return text.toLowerCase();
  }
}

/**
 * Where the form's settings point: what the server compares to decide whether a stored secret may be
 * reused. A path or URL base is not part of it, because it stays on the same host.
 */
// Shared with the pages that render a provider's form, so the fast-refresh export rule does not apply.
// eslint-disable-next-line react-refresh/only-export-components
export function targetOf(fields: NotificationFieldResource[], values: Record<string, unknown>): string {
  // The server decides which fields are the target and sends the flag; a secret that is itself the address has none.
  return fields
    .filter((field) => field.target && !field.secret)
    .map((field) => {
      const raw = values[field.name];

      if (field.type === 'checkbox') {
        return raw === true ? 'ssl' : 'plain';
      }

      const text = typeof raw === 'string' || typeof raw === 'number' ? String(raw) : '';

      // A blank value of a field that has a default is that default (an empty SMTP port is 587), the
      // same rule the server applies, so blank and 587 are one target.
      return urlTarget(text.trim() === '' && field.targetDefault ? field.targetDefault : text);
    })
    .join('|');
}

/** One settings value as the provider reads it: the types the JSON body carries, not the form's. */
// eslint-disable-next-line react-refresh/only-export-components
export function wireValue(field: NotificationFieldResource, value: unknown, masked: boolean): unknown {
  if (field.secret && masked && (value === '' || value === undefined || value === null)) {
    return SECRET_MASK;
  }

  switch (field.type) {
    case 'checkbox':
      return value === true;
    case 'number': {
      // An emptied number field is "not set", which the provider reads as its default.
      if (value === '' || value === undefined || value === null) {
        return null;
      }

      const number = typeof value === 'number' ? value : Number(value);

      return Number.isFinite(number) ? number : null;
    }
    case 'keyValueList':
      return asPairs(value).filter((pair) => pair.key.trim() !== '');
    default:
      return value ?? '';
  }
}

/** What a `keyValueList` field's two columns are called; a header list's are "Name" and "Value". */
export interface PairLabels {
  key: string;
  value: string;
}

const DEFAULT_PAIR_LABELS: PairLabels = { key: 'Name', value: 'Value' };

/** One `keyValueList` field: a row of key/value inputs per entry, with add and remove. */
export function KeyValueListField({
  field,
  pairs,
  error,
  labels = DEFAULT_PAIR_LABELS,
  onChange,
}: {
  field: NotificationFieldResource;
  pairs: NotificationKeyValue[];
  error?: string;
  labels?: PairLabels;
  onChange: (pairs: NotificationKeyValue[]) => void;
}) {
  const replace = (index: number, next: NotificationKeyValue) =>
    onChange(pairs.map((pair, position) => (position === index ? next : pair)));

  return (
    <Stack gap="xs">
      <Text size="sm" fw={500}>
        {field.label}
      </Text>

      {field.helpText !== null && (
        <Text size="xs" c="dimmed">
          {field.helpText}
        </Text>
      )}

      {pairs.map((pair, index) => (
        <Group key={index} gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            aria-label={`${field.label} ${labels.key.toLowerCase()} ${String(index + 1)}`}
            placeholder={labels.key}
            value={pair.key}
            onChange={(event) => replace(index, { ...pair, key: event.currentTarget.value })}
          />
          <TextInput
            aria-label={`${field.label} ${labels.value.toLowerCase()} ${String(index + 1)}`}
            placeholder={labels.value}
            value={pair.value}
            onChange={(event) => replace(index, { ...pair, value: event.currentTarget.value })}
          />
          <ActionIcon
            variant="subtle"
            color="red"
            aria-label={`Remove ${field.label} row ${String(index + 1)}`}
            onClick={() => onChange(pairs.filter((_, position) => position !== index))}
          >
            <Trash2 size={16} />
          </ActionIcon>
        </Group>
      ))}

      <Group>
        <Button variant="light" size="xs" onClick={() => onChange([...pairs, { key: '', value: '' }])}>
          Add {field.label.toLowerCase()} row
        </Button>
      </Group>

      {error !== undefined && (
        <Text size="xs" c="red">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/** One schema field, rendered by the `type` the server named. */
export function SettingsField({
  field,
  value,
  masked,
  reenter = false,
  cleared = false,
  error,
  pairLabels,
  onChange,
  onClear,
  onUndoClear,
}: {
  field: NotificationFieldResource;
  value: unknown;
  masked: boolean;
  /** The saved address changed, so a masked secret must be typed again: the server will not reuse it. */
  reenter?: boolean;
  /** The stored secret is marked to be removed on save; the field shows that instead of an input. */
  cleared?: boolean;
  error?: string;
  pairLabels?: PairLabels;
  onChange: (value: unknown) => void;
  /**
   * Offers a "Clear" action next to an optional secret that holds a stored value. Only the notification
   * form passes it; a form that does not has no way to clear a secret.
   */
  onClear?: () => void;
  /** Takes the clear back. Passed with `onClear`. */
  onUndoClear?: () => void;
}) {
  const mustReenter = field.secret && masked && reenter;
  const label = field.required || mustReenter ? `${field.label} *` : field.label;
  const description = mustReenter
    ? `Enter it again: the address changed. ${field.helpText ?? ''}`.trim()
    : (field.helpText ?? undefined);

  // A secret is always a password input, whatever the type says: the value is never shown, and the
  // mask is what the empty field stands for. Discord's webhook URL is a secret *and* a URL.
  if (field.secret) {
    // Only an optional secret that is stored can be cleared; a required one can only be replaced.
    const clearable = onClear !== undefined && !field.required && masked;

    if (clearable && cleared) {
      return (
        <Stack gap={4}>
          <Text size="sm" fw={500}>
            {label}
          </Text>
          <Group gap="xs">
            <Text size="sm" c="dimmed">
              Will be cleared on save
            </Text>
            <Button
              variant="subtle"
              size="compact-sm"
              aria-label={`Undo clearing ${field.label}`}
              onClick={onUndoClear}
            >
              Undo
            </Button>
          </Group>
        </Stack>
      );
    }

    const input = (
      <PasswordInput
        label={label}
        description={description}
        placeholder={masked && !mustReenter ? SECRET_MASK : undefined}
        value={typeof value === 'string' ? value : ''}
        error={error}
        style={clearable ? { flex: 1 } : undefined}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
    );

    if (!clearable) {
      return input;
    }

    return (
      <Group align="flex-end" wrap="nowrap" gap="xs">
        {input}
        <Button variant="subtle" color="red" size="compact-sm" aria-label={`Clear ${field.label}`} onClick={onClear}>
          Clear
        </Button>
      </Group>
    );
  }

  switch (field.type) {
    case 'password':
      // A password field the provider did not mark secret still reads as a password.
      return (
        <PasswordInput
          label={label}
          description={description}
          value={typeof value === 'string' ? value : ''}
          error={error}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      );
    case 'select':
      return (
        <Select
          label={label}
          description={description}
          data={field.options ?? []}
          value={typeof value === 'string' ? value : null}
          error={error}
          allowDeselect={false}
          onChange={(next) => onChange(next ?? '')}
        />
      );
    case 'number':
      return (
        <NumberInput
          label={label}
          description={description}
          value={typeof value === 'number' || typeof value === 'string' ? value : ''}
          error={error}
          onChange={onChange}
        />
      );
    case 'checkbox':
      return (
        <Checkbox
          label={label}
          description={description}
          checked={value === true}
          error={error}
          onChange={(event) => onChange(event.currentTarget.checked)}
        />
      );
    case 'keyValueList':
      return (
        <KeyValueListField field={field} pairs={asPairs(value)} error={error} labels={pairLabels} onChange={onChange} />
      );
    default:
      return (
        <TextInput
          label={label}
          description={description}
          type={field.type === 'url' ? 'url' : 'text'}
          value={typeof value === 'string' ? value : ''}
          error={error}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      );
  }
}
