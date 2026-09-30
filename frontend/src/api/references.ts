import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { firstPage, pagingValues, type Paging } from './paging';
import { validationFields, ValidationError } from './profiles';
import type { components } from './schema';

/**
 * The reference libraries and the Match queue (LIBRARY_OUTPUT §7.6): the folders the user already has,
 * the files identification could not settle, and the choices that settle them.
 */

export type ReferenceLibraryResource = components['schemas']['ReferenceLibraryResource'];
export type ReferenceLibraryInputResource = components['schemas']['ReferenceLibraryInputResource'];
export type ReferenceLibraryCountsResource = components['schemas']['ReferenceLibraryCountsResource'];
export type MatchQueueItemResource = components['schemas']['MatchQueueItemResource'];
export type MatchCandidateResource = components['schemas']['MatchCandidateResource'];
export type MatchFileResource = components['schemas']['MatchFileResource'];
export type MatchQueuePage = components['schemas']['PagingResourceOfMatchQueueItemResource'];
export type MatchResolveResource = components['schemas']['MatchResolveResource'];
export type MatchBulkResource = components['schemas']['MatchBulkResource'];
export type CommandResource = components['schemas']['CommandResource'];

/**
 * The wire spelling of `ReferenceLibraryMode`. The OpenAPI document types the enum as a string here,
 * but the API serialises camelCase names, so these are the values that actually travel.
 */
export type ReferenceLibraryModeName = 'reference' | 'adopt';

/** The wire spelling of `ReferenceFileState`; see {@link ReferenceLibraryModeName}. */
export type ReferenceFileStateName = 'ambiguous' | 'unmatched';

/** The query keys the reference screens invalidate. A resolve moves the counts and the queue together. */
export const REFERENCE_LIBRARIES_QUERY_KEY = ['reference-libraries'] as const;
export const MATCH_QUEUE_QUERY_KEY = ['match-queue'] as const;

/** How often the reference-library list refreshes while the page is open, in milliseconds. */
export const REFERENCE_LIBRARY_REFETCH_MS = 5000;

/** The values add/edit sends: the resource the endpoint reads, with the mode as its wire name. */
export interface ReferenceLibraryInput {
  name: string;
  rootPath: string;
  mode: ReferenceLibraryModeName;
  /** The managed library found songs are filed under, or `null` for none. */
  libraryId: number | null;
  enabled: boolean;
}

/**
 * What one resolve asks for: exactly one of a candidate's rank, an id the user picked from a manual
 * search, or "leave this file alone".
 */
export type MatchResolveInput =
  | { id: number; candidateRank: number }
  | { id: number; mbRecordingId: string }
  | { id: number; deezerId: number }
  | { id: number; skip: true };

/**
 * Turns a failed response into the error the forms render, reusing the problem reader the other forms
 * share. The body is the one the client has already parsed: the response's own body is consumed by
 * then, so re-reading it would lose the field messages the server sent.
 */
function problemError(status: number, body: unknown, fallback: string): ValidationError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const message = typeof detail === 'string' && detail !== '' ? detail : fallback;

  return new ValidationError(status, message, validationFields(body));
}

/**
 * The Match queue's query as something the typed client accepts. The document describes only the
 * `referenceLibraryId` filter, and the controller reads the paging values off the request by hand, so
 * the generated query type is narrower than what actually travels. The assertion belongs here rather
 * than at the call site.
 */
function matchQuery(values: Record<string, string | number>): never {
  return values as never;
}

/** Everything a scan, an add, a delete or a resolve changes. */
function invalidateReferences(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: REFERENCE_LIBRARIES_QUERY_KEY });
  void queryClient.invalidateQueries({ queryKey: MATCH_QUEUE_QUERY_KEY });
}

/** Every reference library, with how many of its files are in each state. */
export function useReferenceLibraries(): UseQueryResult<ReferenceLibraryResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: REFERENCE_LIBRARIES_QUERY_KEY,
    // A scan walks the folder in the background and reports through the row's counts and last scan
    // line, so the list is polled while the page is open rather than fetched once.
    refetchInterval: REFERENCE_LIBRARY_REFETCH_MS,
    queryFn: async (): Promise<ReferenceLibraryResource[]> => {
      const { data, response } = await client.GET('/api/v1/referencelibrary');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The reference libraries request failed.');
      }

      return data;
    },
  });
}

/** Adds a reference library. */
export function useAddReferenceLibrary(): UseMutationResult<ReferenceLibraryResource, Error, ReferenceLibraryInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: ReferenceLibraryInput): Promise<ReferenceLibraryResource> => {
      const { data, error, response } = await client.POST('/api/v1/referencelibrary', { body: input });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The reference library could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/** Replaces a reference library's settings. */
export function useUpdateReferenceLibrary(): UseMutationResult<
  ReferenceLibraryResource,
  Error,
  ReferenceLibraryInput & { id: number }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, ...input }: ReferenceLibraryInput & { id: number }): Promise<ReferenceLibraryResource> => {
      const { data, error, response } = await client.PUT('/api/v1/referencelibrary/{id}', {
        params: { path: { id } },
        body: input,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The reference library could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/** Deletes a reference library; its files are never touched on disk. */
export function useDeleteReferenceLibrary(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/referencelibrary/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The reference library could not be deleted.');
      }
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/**
 * Queues a scan of one reference library. The queue deduplicates by command name, so a scan asked for
 * while one is already queued returns that command rather than queueing a second.
 */
export function useScanReferenceLibrary(): UseMutationResult<CommandResource, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<CommandResource> => {
      const { data, error, response } = await client.POST('/api/v1/referencelibrary/{id}/scan', {
        params: { path: { id } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The scan could not be queued.');
      }

      return data;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/**
 * Queues the adopt command for one reference library, or for every enabled adopt library when no id
 * is given. Adoption copies each identified file into its target library; the scan's own rows are
 * what the page watches for progress.
 */
export function useAdoptReferenceLibraries(): UseMutationResult<CommandResource, Error, number | null> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (referenceLibraryId: number | null): Promise<CommandResource> => {
      // The command endpoint keeps the whole request body as the command's body and
      // `ReferenceAdoptCommandHandler` reads `referenceLibraryId` off it, so the id travels beside
      // `name` rather than nested under a `body` member.
      const request =
        referenceLibraryId === null ? { name: 'ReferenceAdopt' } : { name: 'ReferenceAdopt', referenceLibraryId };

      const { data, response } = await client.POST('/api/v1/command', { body: request });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The adopt command could not be queued.');
      }

      return data as CommandResource;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/** The files identification could not settle, best path first, optionally of one library only. */
export function useMatchQueue(
  paging: Paging,
  referenceLibraryId?: number | null,
): UseQueryResult<MatchQueuePage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...MATCH_QUEUE_QUERY_KEY, paging, referenceLibraryId ?? null],
    queryFn: async (): Promise<MatchQueuePage> => {
      const query: Record<string, string | number> = pagingValues(paging);

      if (referenceLibraryId !== undefined && referenceLibraryId !== null) {
        query.referenceLibraryId = referenceLibraryId;
      }

      const { data, response } = await client.GET('/api/v1/matchqueue', {
        params: { query: matchQuery(query) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The Match queue request failed.');
      }

      return data;
    },
  });
}

/** How many files are waiting in the Match queue, or `null` while that is unknown. */
export function useMatchQueueTotal(): number | null {
  const client = useApiClient();

  const query = useQuery({
    // A single row is enough: the header badge needs the total, not the page.
    queryKey: [...MATCH_QUEUE_QUERY_KEY, 'total'],
    queryFn: async (): Promise<number> => {
      const { data, response } = await client.GET('/api/v1/matchqueue', {
        params: { query: matchQuery(pagingValues(firstPage(1))) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The Match queue request failed.');
      }

      return Number(data.totalRecords);
    },
  });

  return query.data === undefined || !Number.isFinite(query.data) ? null : query.data;
}

/** Settles one file: a ranked candidate, an id picked from a search, or "leave it alone". */
export function useResolveMatch(): UseMutationResult<MatchResolveResource, Error, MatchResolveInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: MatchResolveInput): Promise<MatchResolveResource> => {
      // Exactly one of the four choices, as the endpoint requires; the document marks all of them
      // required because every member of the record is nullable except `skip`.
      const body = {
        candidateRank: 'candidateRank' in input ? input.candidateRank : null,
        mbRecordingId: 'mbRecordingId' in input ? input.mbRecordingId : null,
        deezerId: 'deezerId' in input ? input.deezerId : null,
        skip: 'skip' in input,
      };

      const { data, error, response } = await client.POST('/api/v1/matchqueue/{id}/resolve', {
        params: { path: { id: input.id } },
        body,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The file could not be resolved.');
      }

      return data;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}

/** Accepts the best candidate of every listed file, in one go. */
export function useBulkAcceptMatches(): UseMutationResult<MatchBulkResource, Error, number[]> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (ids: number[]): Promise<MatchBulkResource> => {
      const { data, error, response } = await client.POST('/api/v1/matchqueue/bulk', {
        body: { ids },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The selected files could not be accepted.');
      }

      return data;
    },
    onSuccess: () => invalidateReferences(queryClient),
  });
}
