import { Anchor, Code, List, Stack, Text, Title } from '@mantine/core';
import type { ReactNode } from 'react';
import { safeHttpUrl } from './text';

/**
 * A small renderer for the Markdown subset GitHub release notes use here (the headings, bullet lists,
 * bold, inline code and links of CHANGELOG.md), built from React elements. It never uses
 * `dangerouslySetInnerHTML`: every piece of text goes through React, which escapes it,
 * and a link is rendered only when its address is http or https. Anything it does not understand is
 * shown as plain text.
 */

function ExternalLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <Anchor href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </Anchor>
  );
}

/** `**bold**`, `` `code` ``, `[text](url)` and a bare http(s) address, in the order they appear. */
const INLINE = /\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)|(https?:\/\/[^\s<>)\]]+)/g;

/** Renders one line of text; an unmatched marker is left as the text it is. */
function renderInline(text: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  let last = 0;
  let key = 0;

  for (const match of text.matchAll(INLINE)) {
    const index = match.index;

    if (index > last) {
      nodes.push(text.slice(last, index));
    }

    const [whole, bold, code, label, target, bare] = match;

    if (bold !== undefined) {
      nodes.push(<strong key={key++}>{renderInline(bold)}</strong>);
    } else if (code !== undefined) {
      nodes.push(<Code key={key++}>{code}</Code>);
    } else if (label !== undefined && target !== undefined) {
      const href = safeHttpUrl(target);

      // A link to anything but http(s) (javascript:, data:, …) is shown as its text, not as a link.
      nodes.push(
        href === null ? (
          label
        ) : (
          <ExternalLink key={key++} href={href}>
            {label}
          </ExternalLink>
        ),
      );
    } else if (bare !== undefined) {
      const href = safeHttpUrl(bare);

      nodes.push(
        href === null ? (
          bare
        ) : (
          <ExternalLink key={key++} href={href}>
            {bare}
          </ExternalLink>
        ),
      );
    } else {
      nodes.push(whole);
    }

    last = index + whole.length;
  }

  if (last < text.length) {
    nodes.push(text.slice(last));
  }

  return nodes;
}

type Block =
  | { kind: 'heading'; level: number; text: string }
  | { kind: 'list'; items: string[] }
  | { kind: 'code'; lines: string[] }
  | { kind: 'paragraph'; text: string };

const HEADING = /^(#{1,6})\s+(.*?)\s*#*\s*$/;
const BULLET = /^\s*[-*+]\s+(.*)$/;
const FENCE = /^\s*```/;

function parse(markdown: string): Block[] {
  const blocks: Block[] = [];
  const lines = markdown.replace(/\r\n?/g, '\n').split('\n');
  let paragraph: string[] = [];
  let list: string[] | null = null;
  let fence: string[] | null = null;

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push({ kind: 'paragraph', text: paragraph.join(' ') });
      paragraph = [];
    }
  };

  const flushList = () => {
    if (list !== null) {
      blocks.push({ kind: 'list', items: list });
      list = null;
    }
  };

  for (const line of lines) {
    if (fence !== null) {
      if (FENCE.test(line)) {
        blocks.push({ kind: 'code', lines: fence });
        fence = null;
      } else {
        fence.push(line);
      }

      continue;
    }

    if (FENCE.test(line)) {
      flushParagraph();
      flushList();
      fence = [];
      continue;
    }

    const heading = HEADING.exec(line);

    if (heading !== null) {
      flushParagraph();
      flushList();
      blocks.push({ kind: 'heading', level: heading[1].length, text: heading[2] });
      continue;
    }

    const bullet = BULLET.exec(line);

    if (bullet !== null) {
      flushParagraph();
      list ??= [];
      list.push(bullet[1]);
      continue;
    }

    if (line.trim() === '') {
      flushParagraph();
      flushList();
      continue;
    }

    // A wrapped line belongs to the bullet above it; otherwise it continues the paragraph.
    if (list !== null && /^\s+\S/.test(line)) {
      list[list.length - 1] += ` ${line.trim()}`;
      continue;
    }

    flushList();
    paragraph.push(line.trim());
  }

  if (fence !== null) {
    blocks.push({ kind: 'code', lines: fence });
  }

  flushParagraph();
  flushList();

  return blocks;
}

/** Release notes as headings, lists and paragraphs. Pass the Markdown as it came from GitHub. */
export function Markdown({ source }: { source: string }) {
  const blocks = parse(source);

  return (
    <Stack gap="xs" data-testid="markdown">
      {blocks.map((block, index) => {
        switch (block.kind) {
          case 'heading':
            // The page's own headings are order 2 and 3; release notes start below them.
            return (
              <Title key={index} order={Math.min(block.level + 3, 6) as 4 | 5 | 6}>
                {renderInline(block.text)}
              </Title>
            );
          case 'list':
            return (
              <List key={index} size="sm" spacing={2}>
                {block.items.map((item, itemIndex) => (
                  <List.Item key={itemIndex}>{renderInline(item)}</List.Item>
                ))}
              </List>
            );
          case 'code':
            return (
              <Code key={index} block>
                {block.lines.join('\n')}
              </Code>
            );
          default:
            return (
              <Text key={index} size="sm">
                {renderInline(block.text)}
              </Text>
            );
        }
      })}
    </Stack>
  );
}
