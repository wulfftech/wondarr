import { Card, Center, Group, Pagination, Table, UnstyledButton } from '@mantine/core';
import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import type { ReactNode } from 'react';
import { toggleDirection, type Paging } from '../api/paging';
import { EmptyState, ErrorState, LoadingState } from './DataState';

/** One column of a {@link PagedTable}. */
export interface PagedColumn<T> {
  /** The heading text. */
  label: string;
  /** The sort key the API orders by, or `null` for a column it cannot sort on. */
  sortKey: string | null;
  /** Renders one row's cell. */
  render: (row: T) => ReactNode;
  /** A fixed column width, in pixels. */
  width?: number;
}

interface PagedTableProps<T> {
  columns: PagedColumn<T>[];
  rows: T[];
  /** How many rows the filter matched in total, for the page count. */
  totalRecords: number;
  paging: Paging;
  /** Called with the paging a header click or a page change asks for. */
  onPaging: (paging: Paging) => void;
  isLoading: boolean;
  error: Error | null;
  emptyMessage: string;
  rowKey: (row: T) => string | number;
  /** Tighter rows, for a table with nothing tall in it. */
  compact?: boolean;
}

function SortIcon({ direction }: { direction: 'ascending' | 'descending' }) {
  return direction === 'ascending' ? <ArrowUp size={14} /> : <ArrowDown size={14} />;
}

/**
 * A sortable, paged Mantine table. The page owns the paging state and the query; this renders the
 * header that asks for the next one, the rows, and the loading, empty and error states.
 */
export function PagedTable<T>({
  columns,
  rows,
  totalRecords,
  paging,
  onPaging,
  isLoading,
  error,
  emptyMessage,
  rowKey,
  compact = false,
}: PagedTableProps<T>) {
  if (isLoading) {
    return <LoadingState />;
  }

  if (error !== null) {
    return <ErrorState message={error.message} />;
  }

  if (rows.length === 0) {
    return <EmptyState message={emptyMessage} />;
  }

  const pageCount = Math.max(1, Math.ceil(totalRecords / Math.max(1, paging.pageSize)));

  return (
    <Card withBorder padding="md">
      <Table verticalSpacing={compact ? 4 : 'xs'}>
        <Table.Thead>
          <Table.Tr>
            {columns.map((column) => {
              const sorted = column.sortKey !== null && column.sortKey === paging.sortKey;

              return (
                <Table.Th
                  key={column.label}
                  w={column.width}
                  aria-sort={sorted ? paging.sortDirection : column.sortKey === null ? undefined : 'none'}
                >
                  {column.sortKey === null ? (
                    column.label
                  ) : (
                    <UnstyledButton
                      onClick={() =>
                        onPaging({
                          ...paging,
                          page: 1,
                          sortKey: column.sortKey ?? paging.sortKey,
                          sortDirection: sorted ? toggleDirection(paging.sortDirection) : 'ascending',
                        })
                      }
                    >
                      <Group gap={4} wrap="nowrap">
                        {column.label}
                        {sorted ? (
                          <SortIcon direction={paging.sortDirection} />
                        ) : (
                          <ArrowUpDown size={14} opacity={0.4} />
                        )}
                      </Group>
                    </UnstyledButton>
                  )}
                </Table.Th>
              );
            })}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((row) => (
            <Table.Tr key={rowKey(row)}>
              {columns.map((column) => (
                <Table.Td key={column.label}>{column.render(row)}</Table.Td>
              ))}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      {pageCount > 1 && (
        <Center mt="md">
          <Pagination total={pageCount} value={paging.page} onChange={(page) => onPaging({ ...paging, page })} />
        </Center>
      )}
    </Card>
  );
}
