import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';

/** The tags songs carry, with how many songs carry each. */

export type TagResource = components['schemas']['TagResource'];

export const TAGS_QUERY_KEY = ['tags'] as const;

/** The tags in use, ordered by label. */
export function useTags(): UseQueryResult<TagResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: TAGS_QUERY_KEY,
    queryFn: async (): Promise<TagResource[]> => {
      const { data, response } = await client.GET('/api/v1/tag');

      if (!response.ok || !Array.isArray(data)) {
        throw new ApiError(response.status, 'The tags request failed.');
      }

      return data;
    },
  });
}
