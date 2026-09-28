import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  NumberInput,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import {
  useSaveQualityProfile,
  ValidationError,
  type QualityProfileItemResource,
  type QualityProfileResource,
} from '../../api/profiles';

/** The members the API's validation messages can be attached to. */
const KNOWN_FIELDS = new Set(['id', 'name', 'upgradeAllowed', 'cutoff', 'minScore', 'durationToleranceMs', 'items']);

/** The name a group shows: its own, or the one quality it holds. */
function itemLabel(item: QualityProfileItemResource): string {
  return item.name ?? item.qualities[0]?.name ?? 'Unknown';
}

/** The qualities a file may be cut off at: every quality of an allowed item. */
function cutoffOptions(items: QualityProfileItemResource[]): { value: string; label: string }[] {
  return items
    .filter((item) => item.allowed)
    .flatMap((item) => item.qualities)
    .map((quality) => ({ value: String(quality.id), label: quality.name ?? `#${quality.id}` }));
}

/**
 * The quality profile editor. It edits a copy of the profile and posts the whole resource back;
 * the API rejects what it cannot store and the messages land under the fields that caused them.
 *
 * The API orders items worst → best; the form shows them best first, which is how a user picks a
 * cutoff, and reverses them again on save so the stored order is unchanged.
 */
export function QualityProfileEditor({ profile, onClose }: { profile: QualityProfileResource; onClose: () => void }) {
  const save = useSaveQualityProfile();
  const [name, setName] = useState(profile.name);
  const [upgradeAllowed, setUpgradeAllowed] = useState(profile.upgradeAllowed);
  const [cutoff, setCutoff] = useState(String(profile.cutoff));
  const [minScore, setMinScore] = useState<number | string>(profile.minScore);
  const [durationSeconds, setDurationSeconds] = useState<number | string>(Number(profile.durationToleranceMs) / 1000);
  const [items, setItems] = useState<QualityProfileItemResource[]>(profile.items);

  const fields = save.error instanceof ValidationError ? save.error.fields : {};
  const general = Object.entries(fields).filter(([field]) => !KNOWN_FIELDS.has(field));
  const best = [...items].reverse();

  const setAllowed = (displayIndex: number, allowed: boolean) => {
    const index = items.length - 1 - displayIndex;

    setItems(items.map((item, position) => (position === index ? { ...item, allowed } : item)));
  };

  const submit = () => {
    const body: QualityProfileResource = {
      id: profile.id,
      name,
      upgradeAllowed,
      cutoff: Number(cutoff),
      minScore: Number(minScore),
      durationToleranceMs: Math.round(Number(durationSeconds) * 1000),
      items,
    };

    save.mutate(body, { onSuccess: () => onClose() });
  };

  return (
    <Stack gap="md">
      <TextInput
        label="Name"
        value={name}
        error={fields.name}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      <Switch
        label="Upgrades allowed"
        checked={upgradeAllowed}
        onChange={(event) => setUpgradeAllowed(event.currentTarget.checked)}
      />

      <Stack gap="xs">
        <Text size="sm" fw={500}>
          Qualities, best first
        </Text>

        {best.map((item, displayIndex) => (
          <Group key={itemLabel(item)} gap="sm" wrap="nowrap" align="center">
            <Checkbox
              aria-label={`${itemLabel(item)} allowed`}
              checked={item.allowed}
              onChange={(event) => setAllowed(displayIndex, event.currentTarget.checked)}
            />
            <Text size="sm" w={160}>
              {itemLabel(item)}
            </Text>
            <Group gap={4}>
              {item.qualities.map((quality) => (
                <Badge key={String(quality.id)} variant="light" color="gray">
                  {quality.name ?? `#${quality.id}`}
                </Badge>
              ))}
            </Group>
          </Group>
        ))}

        {fields.items !== undefined && (
          <Text size="xs" c="red">
            {fields.items}
          </Text>
        )}
      </Stack>

      <Select
        label="Cutoff"
        description="The quality that counts as done. Only qualities in allowed groups are offered."
        data={cutoffOptions(items)}
        value={cutoff}
        error={fields.cutoff}
        allowDeselect={false}
        onChange={(value) => setCutoff(value ?? cutoff)}
      />

      <NumberInput
        label="Minimum score"
        min={0}
        max={1000}
        value={minScore}
        error={fields.minScore}
        onChange={setMinScore}
      />

      <NumberInput
        label="Duration tolerance"
        description="How far a candidate's length may differ, in seconds."
        min={0}
        max={60}
        value={durationSeconds}
        error={fields.durationToleranceMs}
        onChange={setDurationSeconds}
      />

      {general.map(([field, message]) => (
        <Alert key={field} color="red" icon={<CircleAlert size={16} />} title={field}>
          {message}
        </Alert>
      ))}

      {save.error !== null && general.length === 0 && save.error.message !== '' && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {save.error.message}
        </Alert>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button loading={save.isPending} onClick={submit}>
          Save
        </Button>
      </Group>
    </Stack>
  );
}
