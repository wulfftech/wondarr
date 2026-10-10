import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { theme } from '../../theme/theme';
import { AlbumCover } from './AlbumOptionLabel';

afterEach(() => {
  vi.restoreAllMocks();
});

function renderCover(url: string | null) {
  return render(
    <MantineProvider theme={theme}>
      <AlbumCover url={url} />
    </MantineProvider>,
  );
}

describe('AlbumCover', () => {
  it('shows the placeholder when there is no url', () => {
    renderCover(null);

    expect(screen.getByTestId('cover-placeholder')).toBeInTheDocument();
  });

  it('shows the placeholder once the image errors', () => {
    const { container } = renderCover('https://coverartarchive.org/release/x/front-250');

    expect(screen.queryByTestId('cover-placeholder')).not.toBeInTheDocument();

    fireEvent.error(container.querySelector('img') as HTMLImageElement);

    expect(screen.getByTestId('cover-placeholder')).toBeInTheDocument();
    expect(container.querySelector('img')).toBeNull();
  });

  it('shows the placeholder for an image that had already failed before it mounted', () => {
    vi.spyOn(HTMLImageElement.prototype, 'complete', 'get').mockReturnValue(true);
    vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(0);

    renderCover('https://coverartarchive.org/release/y/front-250');

    expect(screen.getByTestId('cover-placeholder')).toBeInTheDocument();
  });
});
