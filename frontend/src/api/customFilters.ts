import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';
import { problemError } from './songs';

/** Saved views of the list pages (the API's custom filters). */

export type CustomFilterResource = components['schemas']['CustomFilterResource'];

/** One entry of a saved view: the query parameter, its value, and how it compares. */
export interface CustomFilterEntry {
  key: string;
  value: string;
  type: 'equal';
}

/** What a view is saved as. */
export interface CustomFilterInput {
  type: string;
  label: string;
  filters: CustomFilterEntry[];
}

export const CUSTOM_FILTERS_QUERY_KEY = ['custom-filters'] as const;

/** The saved views of one page type, for example `library`. */
export function useCustomFilters(type: string): UseQueryResult<CustomFilterResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...CUSTOM_FILTERS_QUERY_KEY, type],
    queryFn: async (): Promise<CustomFilterResource[]> => {
      const { data, response } = await client.GET('/api/v1/customfilter', { params: { query: { type } } });

      if (!response.ok || !Array.isArray(data)) {
        throw new ApiError(response.status, 'The saved views request failed.');
      }

      return data;
    },
  });
}

/** The entries of a view's filters, ignoring anything that is not a `{ key, value }` pair. */
export function filterEntries(filters: unknown): { key: string; value: string }[] {
  if (!Array.isArray(filters)) {
    return [];
  }

  return filters.flatMap((entry: unknown) => {
    if (typeof entry !== 'object' || entry === null) {
      return [];
    }

    const { key, value } = entry as Record<string, unknown>;

    const scalar = typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';

    return typeof key === 'string' && scalar ? [{ key, value: String(value) }] : [];
  });
}

/** Saves a new view; the API answers 409 when the label is taken. */
export function useCreateCustomFilter(): UseMutationResult<CustomFilterResource, Error, CustomFilterInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: CustomFilterInput): Promise<CustomFilterResource> => {
      const { data, error, response } = await client.POST('/api/v1/customfilter', { body: input });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The view could not be saved.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CUSTOM_FILTERS_QUERY_KEY }),
  });
}

/** Replaces a saved view. */
export function useUpdateCustomFilter(): UseMutationResult<
  CustomFilterResource,
  Error,
  { id: number; input: CustomFilterInput }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: { id: number; input: CustomFilterInput }): Promise<CustomFilterResource> => {
      const { data, error, response } = await client.PUT('/api/v1/customfilter/{id}', {
        params: { path: { id: update.id } },
        body: update.input,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The view could not be saved.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CUSTOM_FILTERS_QUERY_KEY }),
  });
}

/** Deletes a saved view. */
export function useDeleteCustomFilter(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/customfilter/{id}', { params: { path: { id } } });

      if (!response.ok) {
        throw new ApiError(response.status, 'The view could not be deleted.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CUSTOM_FILTERS_QUERY_KEY }),
  });
}
