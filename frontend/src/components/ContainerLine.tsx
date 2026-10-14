import { Text, Tooltip } from '@mantine/core';
import { containerLineText } from './text';

/**
 * The muted second line the Queue and History pages put under a song that came out of a release. It
 * wraps and is cut off after two lines, with the whole text in a tooltip; the break-anywhere rule
 * keeps one long release name from pushing the table wider than a phone.
 */
export function ContainerLine({
  release,
  file,
  bundledCount,
}: {
  release: string;
  file: string | null;
  bundledCount: number;
}) {
  const text = containerLineText(release, file, bundledCount);

  return (
    <Tooltip label={text} multiline maw={420} openDelay={300}>
      <Text size="xs" c="dimmed" lineClamp={2} maw={320} style={{ overflowWrap: 'anywhere' }}>
        {text}
      </Text>
    </Tooltip>
  );
}
