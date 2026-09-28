import { Alert, Badge, Button, Card, Group, Modal, Stack, Text, Title } from '@mantine/core';
import { CircleAlert, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import {
  useDeleteQualityProfile,
  useQualityDefinitions,
  useQualityProfiles,
  type QualityProfileItemResource,
  type QualityProfileResource,
} from '../../api/profiles';
import { ErrorState, LoadingState, EmptyState } from '../../components/DataState';
import { QualityProfileEditor } from './QualityProfileEditor';

/** The name a group shows: its own, or the one quality it holds. */
function itemLabel(item: QualityProfileItemResource): string {
  return item.name ?? item.qualities[0]?.name ?? 'Unknown';
}

/** The profile a new one starts as: a copy of the first, so a user edits rather than types. */
function copyOfFirst(profiles: QualityProfileResource[]): QualityProfileResource {
  const original = profiles[0];

  return original === undefined
    ? {
        id: 0,
        name: 'New profile',
        upgradeAllowed: true,
        cutoff: 0,
        minScore: 0,
        durationToleranceMs: 3000,
        items: [],
      }
    : { ...original, id: 0, name: 'New profile', items: original.items.map((item) => ({ ...item })) };
}

/** Quality profiles: one card per profile, with the editor and the delete confirmation in modals. */
export function QualityProfilesPage() {
  const profiles = useQualityProfiles();
  const qualities = useQualityDefinitions();
  const remove = useDeleteQualityProfile();
  const [editing, setEditing] = useState<QualityProfileResource | null>(null);
  const [deleting, setDeleting] = useState<QualityProfileResource | null>(null);

  if (profiles.isPending) {
    return <LoadingState />;
  }

  if (profiles.error !== null) {
    return <ErrorState message={profiles.error.message} />;
  }

  const qualityNames = new Map((qualities.data ?? []).map((quality) => [String(quality.id), quality.name]));
  const rows = profiles.data ?? [];

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={3}>Quality profiles</Title>
        <Button leftSection={<Plus size={16} />} onClick={() => setEditing(copyOfFirst(rows))}>
          Add profile
        </Button>
      </Group>

      {rows.length === 0 && <EmptyState message="No quality profiles are configured." />}

      {rows.map((profile) => {
        const allowed = profile.items.filter((item) => item.allowed);

        return (
          <Card key={String(profile.id)} withBorder padding="md">
            <Group justify="space-between" align="flex-start">
              <Stack gap={4}>
                <Text fw={600}>{profile.name}</Text>
                <Text size="sm" c="dimmed">
                  Cutoff: {qualityNames.get(String(profile.cutoff)) ?? `#${profile.cutoff}`}
                </Text>
                <Group gap={4}>
                  {allowed.length === 0 ? (
                    <Text size="sm" c="dimmed">
                      No qualities are allowed.
                    </Text>
                  ) : (
                    allowed.map((item) => (
                      <Badge key={itemLabel(item)} variant="light">
                        {itemLabel(item)}
                      </Badge>
                    ))
                  )}
                </Group>
                <Text size="sm">Upgrades {profile.upgradeAllowed ? 'on' : 'off'}</Text>
              </Stack>

              <Group gap="xs">
                <Button
                  variant="light"
                  size="xs"
                  leftSection={<Pencil size={14} />}
                  onClick={() => setEditing(profile)}
                >
                  Edit
                </Button>
                <Button
                  variant="light"
                  color="red"
                  size="xs"
                  leftSection={<Trash2 size={14} />}
                  onClick={() => setDeleting(profile)}
                >
                  Delete
                </Button>
              </Group>
            </Group>
          </Card>
        );
      })}

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={editing?.id === 0 ? 'Add profile' : `Edit ${editing?.name ?? ''}`}
        size="lg"
      >
        {editing !== null && <QualityProfileEditor profile={editing} onClose={() => setEditing(null)} />}
      </Modal>

      <Modal opened={deleting !== null} onClose={() => setDeleting(null)} title="Delete profile">
        <Stack gap="md">
          <Text size="sm">
            Delete “{deleting?.name ?? ''}”? Songs using it have to be given another profile afterwards.
          </Text>

          {remove.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />} title="The profile was not deleted">
              {remove.error.message}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={() => {
                if (deleting !== null) {
                  remove.mutate(Number(deleting.id), { onSuccess: () => setDeleting(null) });
                }
              }}
            >
              Delete
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
