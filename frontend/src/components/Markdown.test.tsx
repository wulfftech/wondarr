import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { describe, expect, it } from 'vitest';
import { Markdown } from './Markdown';
import { safeHttpUrl } from './text';

function renderMarkdown(source: string) {
  return render(
    <MantineProvider env="test">
      <Markdown source={source} />
    </MantineProvider>,
  );
}

describe('Markdown', () => {
  it('renders headings, bullet lists, bold, inline code and links', () => {
    renderMarkdown(
      [
        '## Added',
        '',
        '- the **update checker** for `config.yml`',
        '- see [the docs](https://wondarr.example/docs)',
        '',
        'A closing paragraph.',
      ].join('\n'),
    );

    expect(screen.getByRole('heading', { name: 'Added' })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('update checker').tagName).toBe('STRONG');
    expect(screen.getByText('config.yml').tagName).toBe('CODE');
    expect(screen.getByText('A closing paragraph.')).toBeInTheDocument();

    const link = screen.getByRole('link', { name: 'the docs' });
    expect(link).toHaveAttribute('href', 'https://wondarr.example/docs');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('escapes HTML instead of injecting it', () => {
    const { container } = renderMarkdown('<img src=x onerror="alert(1)"> and <script>alert(2)</script>');

    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('script')).toBeNull();
    expect(container).toHaveTextContent('<img src=x onerror="alert(1)">');
    expect(container).toHaveTextContent('<script>alert(2)</script>');
  });

  it('drops a javascript: link and keeps its text', () => {
    const { container } = renderMarkdown('[click me](javascript:alert(1)) and [data](data:text/html,x)');

    expect(container.querySelector('a')).toBeNull();
    expect(container).toHaveTextContent('click me');
    expect(container.innerHTML).not.toContain('javascript:');
  });

  it('links a bare https address and leaves other text alone', () => {
    renderMarkdown('**Full Changelog**: https://github.com/wulfftech/wondarr/compare/v0.1.0...v0.2.0');

    expect(screen.getByRole('link')).toHaveAttribute(
      'href',
      'https://github.com/wulfftech/wondarr/compare/v0.1.0...v0.2.0',
    );
  });

  it('shows what it does not understand as plain text', () => {
    const { container } = renderMarkdown('| a | b |\n|---|---|\n| 1 | 2 |\n\n> a quote with *emphasis*');

    expect(container).toHaveTextContent('| a | b |');
    expect(container).toHaveTextContent('> a quote with *emphasis*');
  });

  it('renders a fenced block as code', () => {
    renderMarkdown('```\ndocker compose pull\n```');

    expect(screen.getByText('docker compose pull').closest('pre')).not.toBeNull();
  });
});

describe('safeHttpUrl', () => {
  it('accepts http and https and nothing else', () => {
    expect(safeHttpUrl('https://example.org/x')).toBe('https://example.org/x');
    expect(safeHttpUrl('http://example.org/')).toBe('http://example.org/');
    expect(safeHttpUrl('javascript:alert(1)')).toBeNull();
    expect(safeHttpUrl('data:text/html,x')).toBeNull();
    expect(safeHttpUrl('not a url')).toBeNull();
  });
});
