import { Alert, Anchor, Badge, Button, Card, Code, Group, List, Stack, Table, Text, Title } from '@mantine/core';
import { ArrowUpCircle, CircleAlert, CircleCheck, RefreshCw } from 'lucide-react';
import { Link } from 'react-router';
import { Markdown } from '../../components/Markdown';
import { relativeTime, safeHttpUrl } from '../../components/text';
import { ErrorState, LoadingState } from '../../components/DataState';
import { UPDATE_SETTINGS_PATH, useCheckForUpdate, useUpdateStatus, type UpdateStatus } from '../../api/update';

function formatDate(iso: string | null | undefined): string {
  if (iso === null || iso === undefined || iso === '') {
    return '';
  }

  const parsed = new Date(iso);

  return Number.isNaN(parsed.getTime()) ? '' : parsed.toLocaleDateString();
}

function HowToUpdate() {
  return (
    <Stack gap="sm" data-testid="how-to-update">
      <Title order={3}>How to update</Title>
      <Text size="sm" c="dimmed">
        Wondarr runs in a container and never updates itself. Pull the new image and start it again; your{' '}
        <Code>/config</Code> and <Code>/data</Code> folders are kept.
      </Text>

      <Title order={4}>Docker Compose</Title>
      <Code block>{'docker compose pull && docker compose up -d'}</Code>
      <Text size="sm" c="dimmed">
        If the image is pinned to a tag such as <Code>:0.1</Code>, change the tag in your compose file to move to a new
        minor version; <Code>:latest</Code> follows every release.
      </Text>

      <Title order={4}>docker run</Title>
      <List size="sm" spacing={2} type="ordered">
        <List.Item>
          Pull the new image with <Code>docker pull</Code>.
        </List.Item>
        <List.Item>
          Stop and remove the container with <Code>docker stop wondarr &amp;&amp; docker rm wondarr</Code>.
        </List.Item>
        <List.Item>
          Create it again with the same <Code>docker run</Code> command you used the first time.
        </List.Item>
      </List>

      <Title order={4}>Unraid</Title>
      <Text size="sm">
        On the Docker tab, choose Check for updates, then apply the update on the Wondarr container.
      </Text>
    </Stack>
  );
}

function Available({ status }: { status: UpdateStatus }) {
  const url = status.releaseUrl === null ? null : safeHttpUrl(status.releaseUrl);
  const published = formatDate(status.publishedAt);

  return (
    <Card withBorder padding="md" data-testid="update-available">
      <Stack gap="sm">
        <Group gap="sm">
          <ArrowUpCircle size={20} />
          <Title order={3}>{status.releaseName ?? `Wondarr ${status.latestVersion ?? ''}`}</Title>
          <Badge variant="light">{status.latestVersion}</Badge>
        </Group>
        {published !== '' && (
          <Text size="sm" c="dimmed">
            Released {published}
          </Text>
        )}
        {url !== null && (
          <Anchor href={url} target="_blank" rel="noopener noreferrer" size="sm">
            View this release on GitHub
          </Anchor>
        )}
        {status.releaseNotes !== null && status.releaseNotes.trim() !== '' && (
          <Stack gap="xs">
            <Title order={4}>Release notes</Title>
            <Markdown source={status.releaseNotes} />
          </Stack>
        )}
      </Stack>
    </Card>
  );
}

function Verdict({ status }: { status: UpdateStatus }) {
  if (!status.checkEnabled) {
    return (
      <Alert color="gray" icon={<CircleAlert size={16} />} title="Update checks are off">
        Wondarr is not asking GitHub for new releases. Turn it on in{' '}
        <Anchor component={Link} to={UPDATE_SETTINGS_PATH}>
          Settings → General
        </Anchor>
        .
      </Alert>
    );
  }

  if (status.isDevelopmentBuild) {
    return (
      <Alert color="gray" icon={<CircleAlert size={16} />} title="Development build">
        You&apos;re running a development build ({status.currentVersion}).{' '}
        {status.latestVersion === null
          ? 'No release has been found yet.'
          : `The latest release is ${status.latestVersion}.`}
      </Alert>
    );
  }

  if (status.updateAvailable) {
    return null;
  }

  if (status.latestVersion === null) {
    return (
      <Alert color="gray" icon={<CircleAlert size={16} />} title="Not checked yet">
        Wondarr has not found out what the latest release is yet.
      </Alert>
    );
  }

  return (
    <Alert color="green" icon={<CircleCheck size={16} />} title="You're up to date.">
      Wondarr {status.currentVersion} is the latest release.
    </Alert>
  );
}

/** System → Updates: the running and latest versions, the release notes, and how to update. */
export function UpdatesPage() {
  const status = useUpdateStatus();
  const check = useCheckForUpdate();

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Updates</Title>
        <Button
          leftSection={<RefreshCw size={16} />}
          loading={check.isPending}
          disabled={status.data !== undefined && !status.data.checkEnabled}
          onClick={() => check.mutate()}
        >
          Check now
        </Button>
      </Group>

      {status.isPending && <LoadingState />}
      {status.isError && <ErrorState title="The update status could not be loaded" message={status.error.message} />}
      {check.isError && <ErrorState title="The check failed" message={check.error.message} />}

      {status.data !== undefined && (
        <>
          <Card withBorder padding="md">
            <Table data-testid="update-versions">
              <Table.Tbody>
                <Table.Tr>
                  <Table.Td w={220}>
                    <Text fw={500}>Current version</Text>
                  </Table.Td>
                  <Table.Td>{status.data.currentVersion}</Table.Td>
                </Table.Tr>
                <Table.Tr>
                  <Table.Td>
                    <Text fw={500}>Latest version</Text>
                  </Table.Td>
                  <Table.Td>{status.data.latestVersion ?? '—'}</Table.Td>
                </Table.Tr>
                <Table.Tr>
                  <Table.Td>
                    <Text fw={500}>Last checked</Text>
                  </Table.Td>
                  <Table.Td>
                    {status.data.checkEnabled ? `Last checked ${relativeTime(status.data.checkedAt)}` : '—'}
                  </Table.Td>
                </Table.Tr>
              </Table.Tbody>
            </Table>
          </Card>

          {status.data.lastError !== null && (
            <Alert
              color="red"
              icon={<CircleAlert size={16} />}
              title="The last check failed"
              data-testid="update-error"
            >
              {status.data.lastError}
            </Alert>
          )}

          <Verdict status={status.data} />

          {status.data.updateAvailable && status.data.checkEnabled && !status.data.isDevelopmentBuild && (
            <>
              <Available status={status.data} />
              <HowToUpdate />
            </>
          )}
        </>
      )}
    </Stack>
  );
}
