import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { firstPage, type Paging } from '../api/paging';
import { theme } from '../theme/theme';
import { PagedTable, type PagedColumn } from './PagedTable';

const COLUMNS: PagedColumn<{ id: number; name: string }>[] = [
  { label: 'Name', sortKey: null, render: (row) => row.name },
];

/**
 * A table over a server that holds `total` rows, 2 to a page. The page owns the paging state, as the
 * real ones do; `total` is what the "server" has left after the user deleted rows.
 */
function Harness({ total, startPage }: { total: number; startPage: number }) {
  const [paging, setPaging] = useState<Paging>({ ...firstPage(2), page: startPage });
  const first = (paging.page - 1) * 2;
  const rows = Array.from({ length: Math.max(0, Math.min(2, total - first)) }, (_, index) => ({
    id: first + index,
    name: `Row ${String(first + index + 1)}`,
  }));

  return (
    <MantineProvider theme={theme} env="test">
      <PagedTable
        columns={COLUMNS}
        rows={rows}
        totalRecords={total}
        paging={paging}
        onPaging={setPaging}
        isLoading={false}
        error={null}
        emptyMessage="Nothing here."
        rowKey={(row) => row.id}
      />
    </MantineProvider>
  );
}

describe('PagedTable', () => {
  it('lands on page 1 when everything on page 2 was deleted', async () => {
    // Two rows are left, which is one full page; the user was on page 2.
    render(<Harness total={2} startPage={2} />);

    expect(await screen.findByText('Row 1')).toBeInTheDocument();
    expect(screen.queryByText('Nothing here.')).not.toBeInTheDocument();
  });

  it('lands on the last page that has rows when the total has shrunk a long way', async () => {
    // Four rows, 2 to a page: two pages. The user was on page 3.
    render(<Harness total={4} startPage={3} />);

    expect(await screen.findByText('Row 3')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: '2' })).toHaveAttribute('data-active', 'true'));
  });

  it('shows the empty state when there are no rows at all', () => {
    render(<Harness total={0} startPage={1} />);

    expect(screen.getByText('Nothing here.')).toBeInTheDocument();
  });
});
