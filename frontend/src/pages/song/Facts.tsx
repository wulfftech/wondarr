import { Anchor, Card, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { ExternalLink } from 'lucide-react';
import type { ReactNode } from 'react';
import { safeUrl } from '../../components/song/format';

/** The small layout pieces the song page's tabs share. */

/** A titled card; the tabs use one per source. */
export function Block({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card withBorder padding="md" component="section" aria-label={title}>
      <Stack gap="sm">
        <Title order={4}>{title}</Title>
        {children}
      </Stack>
    </Card>
  );
}

/** A label above a value; a `null` value is left out by the caller. */
export function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text size="sm" component="div" style={{ overflowWrap: 'anywhere' }}>
        {children}
      </Text>
    </div>
  );
}

/** Facts in a grid that stacks to one column on a phone. */
export function FactGrid({ children }: { children: ReactNode }) {
  return (
    <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="md" verticalSpacing="sm">
      {children}
    </SimpleGrid>
  );
}

/** A link out to a source's own page; nothing at all when the URL is not an http(s) one. */
export function SourceLink({ url, children }: { url: string | null | undefined; children: ReactNode }) {
  const href = safeUrl(url);

  if (href === null) {
    return null;
  }

  return (
    <Anchor href={href} target="_blank" rel="noreferrer noopener" size="sm">
      {children} <ExternalLink size={12} aria-hidden style={{ verticalAlign: 'baseline' }} />
    </Anchor>
  );
}
