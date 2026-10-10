import { Badge, CloseButton, Group } from '@mantine/core';
import { ArrowUpCircle } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { useUpdateStatus } from '../api/update';

/** The one `localStorage` key: the newest version the user dismissed the badge for. */
export const DISMISSED_UPDATE_KEY = 'wondarr.update.dismissedVersion';

function readDismissed(): string | null {
  try {
    return window.localStorage.getItem(DISMISSED_UPDATE_KEY);
  } catch {
    // Private windows and blocked site data throw; the badge then simply cannot stay dismissed.
    return null;
  }
}

function writeDismissed(version: string): void {
  try {
    window.localStorage.setItem(DISMISSED_UPDATE_KEY, version);
  } catch {
    // See readDismissed.
  }
}

/**
 * "Update available", beside the health badge. It links to System → Updates and can be dismissed for
 * that version only: a newer release shows it again. Never shown for a development build, while the
 * check is off, or before the first check has found anything.
 */
export function UpdateBadge() {
  const status = useUpdateStatus();
  const [dismissed, setDismissed] = useState<string | null>(readDismissed);

  const update = status.data;

  if (
    update === undefined ||
    !update.updateAvailable ||
    update.isDevelopmentBuild ||
    !update.checkEnabled ||
    update.latestVersion === null ||
    update.latestVersion === dismissed
  ) {
    return null;
  }

  const latest = update.latestVersion;

  return (
    <Group gap={4} wrap="nowrap" data-testid="update-badge">
      <Badge
        component={Link}
        to="/system/updates"
        variant="light"
        color="blue"
        leftSection={<ArrowUpCircle size={12} />}
        style={{ cursor: 'pointer' }}
        title={`Wondarr ${latest} is available (you have ${update.currentVersion})`}
      >
        Update available
      </Badge>
      <CloseButton
        size="xs"
        aria-label={`Dismiss the notice about ${latest}`}
        onClick={() => {
          writeDismissed(latest);
          setDismissed(latest);
        }}
      />
    </Group>
  );
}
