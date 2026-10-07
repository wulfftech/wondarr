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

/** The quality ladder, the profiles built from it and the libraries songs are filed in. */

export type QualityDefinitionResource = components['schemas']['QualityDefinitionResource'];
export type QualityProfileResource = components['schemas']['QualityProfileResource'];
export type QualityProfileItemResource = components['schemas']['QualityProfileItemResource'];
export type LibraryResource = components['schemas']['LibraryResource'];
export type LibraryPreviewResource = components['schemas']['LibraryPreviewResource'];

/** The query keys the profile and library pages invalidate. */
export const QUALITY_DEFINITIONS_QUERY_KEY = ['quality-definitions'] as const;
export const QUALITY_PROFILES_QUERY_KEY = ['quality-profiles'] as const;
export const LIBRARIES_QUERY_KEY = ['libraries'] as const;

/**
 * The wire spelling of `LibraryLayout`. The OpenAPI document types the enum as an integer, but the
 * API serialises enums as camelCase strings (`LibraryResource` says so in as many words), so these
 * are the values that actually travel.
 */
export type LibraryLayoutName = 'flat' | 'artist' | 'artistAlbum' | 'plexamp';

/** The wire spelling of `AlbumPolicy`; see {@link LibraryLayoutName}. */
export type AlbumPolicyName = 'fewestAlbums' | 'singlesOnly' | 'originalAlbum' | 'singleRelease' | 'compilation';

/**
 * The conversion rule one source class carries, with every key the library's save sends
 * (LIBRARY_OUTPUT §7.7). A key the rule does not use reads and writes `"keep"`.
 */
export interface OutputRule {
  /** What the file becomes: `keep`, or the codec it is re-encoded to. */
  codec: OutputCodecName;
  /** How the target bitrate is chosen: a fixed `bitrateKbps`, or the LAME quality scale (MP3 only). */
  mode: OutputModeName;
  /** The constant bitrate in kbps, or `"keep"` when the rule does not use one. */
  bitrateKbps: number | 'keep';
  /** The LAME VBR quality, 0 (best) to 9 (smallest), or `"keep"` when the rule does not use one. */
  vbrQuality: number | 'keep';
  /** The target sample rate in Hz, or `"keep"` to keep the source's. */
  sampleRate: number | 'keep';
  /** The container an Opus target — or a kept Opus file — is named with. */
  opusContainer: OpusContainerName;
}

/** The conversion rules the library's output policy carries, one per source class. */
export interface OutputPolicy {
  version: 2;
  youtube: OutputRule;
  lossy: OutputRule;
  lossless: OutputRule;
}

/** The wire spelling of `OutputCodec`; see {@link LibraryLayoutName}. */
export type OutputCodecName = 'keep' | 'aac' | 'mp3' | 'opus' | 'flac' | 'alac';

/** The wire spelling of `OutputMode`; see {@link LibraryLayoutName}. */
export type OutputModeName = 'cbr' | 'vbr';

/** The wire spelling of the Opus container; see {@link LibraryLayoutName}. */
export type OpusContainerName = 'opus' | 'ogg';

/** The source classes the policy carries one rule for, with the labels the form shows them by. */
export const OUTPUT_RULE_SOURCES: { key: keyof Omit<OutputPolicy, 'version'>; label: string }[] = [
  { key: 'youtube', label: 'YouTube downloads' },
  { key: 'lossy', label: 'Lossy files (MP3, AAC, Opus, Vorbis…)' },
  { key: 'lossless', label: 'Lossless files (FLAC, ALAC, WAV…)' },
];

/** The codecs a rule that is not a lossless source's can name, in the order the form offers them. */
export const LOSSY_CODECS: { value: OutputCodecName; label: string }[] = [
  { value: 'keep', label: 'Keep as downloaded' },
  { value: 'aac', label: 'AAC (.m4a)' },
  { value: 'mp3', label: 'MP3' },
  { value: 'opus', label: 'Opus' },
];

/** The two codecs only a lossless source may be converted to (ADR-0006: never lossless from lossy). */
export const LOSSLESS_ONLY_CODECS: { value: OutputCodecName; label: string }[] = [
  { value: 'flac', label: 'FLAC' },
  { value: 'alac', label: 'ALAC (.m4a)' },
];

/** The constant bitrates the form offers, in kbps. */
export const BITRATE_OPTIONS = [128, 160, 192, 256, 320];

/** The LAME VBR qualities the form offers, 0 (best) to 9 (smallest). */
export const VBR_QUALITY_OPTIONS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

/** The sample rates the form offers besides keeping the source's. */
export const SAMPLE_RATE_OPTIONS = [44100, 48000];

/** The rule a source class without one falls back to: the file is kept as it was served. */
export function keepRule(): OutputRule {
  return {
    codec: 'keep',
    mode: 'cbr',
    bitrateKbps: 'keep',
    vbrQuality: 'keep',
    sampleRate: 'keep',
    opusContainer: 'opus',
  };
}

/** The policy a library without one reports: YouTube → AAC 256 kbps CBR, the rest kept (§7.7). */
export function defaultOutputPolicy(): OutputPolicy {
  return {
    version: 2,
    youtube: {
      codec: 'aac',
      mode: 'cbr',
      bitrateKbps: 256,
      vbrQuality: 'keep',
      sampleRate: 'keep',
      opusContainer: 'opus',
    },
    lossy: keepRule(),
    lossless: keepRule(),
  };
}

/** The codecs one row offers: the lossless row also takes FLAC and ALAC. */
export function codecOptions(source: keyof Omit<OutputPolicy, 'version'>): { value: OutputCodecName; label: string }[] {
  return source === 'lossless' ? [...LOSSY_CODECS, ...LOSSLESS_ONLY_CODECS] : LOSSY_CODECS;
}

/** Reads one rule the policy may not carry, or may not spell the way the form expects. */
function readRule(value: unknown, source: keyof Omit<OutputPolicy, 'version'>): OutputRule {
  if (typeof value !== 'object' || value === null) {
    return keepRule();
  }

  const rule = value as Record<string, unknown>;
  const allowed = codecOptions(source).map((option) => option.value);
  const codec = allowed.includes(rule.codec as OutputCodecName) ? (rule.codec as OutputCodecName) : 'keep';
  const bitrate =
    typeof rule.bitrateKbps === 'number' && rule.bitrateKbps >= 64 && rule.bitrateKbps <= 320
      ? rule.bitrateKbps
      : 'keep';
  const vbr =
    typeof rule.vbrQuality === 'number' && rule.vbrQuality >= 0 && rule.vbrQuality <= 9 ? rule.vbrQuality : 'keep';
  const sampleRate = typeof rule.sampleRate === 'number' && rule.sampleRate > 0 ? rule.sampleRate : 'keep';

  return {
    codec,
    // VBR is the LAME quality scale, so only an MP3 target can carry it; anything else reads CBR.
    mode: codec === 'mp3' && rule.mode === 'vbr' ? 'vbr' : 'cbr',
    // A converting rule always shows a bitrate and a quality, so a missing one takes the default.
    bitrateKbps: codec === 'keep' ? 'keep' : bitrate === 'keep' ? 256 : bitrate,
    vbrQuality: codec === 'mp3' ? (vbr === 'keep' ? 0 : vbr) : 'keep',
    sampleRate: codec === 'keep' ? 'keep' : sampleRate,
    opusContainer: rule.opusContainer === 'ogg' ? 'ogg' : 'opus',
  };
}

/**
 * Reads the library's stored `outputPolicy` (a `JsonElement`, so untyped on the wire) into the
 * version-2 shape the form edits. A library without a policy reports the default (§7.7); a
 * version-1 object — the flat Phase 4 shape — is the YouTube rule with the other two kept.
 */
export function readOutputPolicy(value: unknown): OutputPolicy {
  if (typeof value !== 'object' || value === null) {
    return defaultOutputPolicy();
  }

  const policy = value as Record<string, unknown>;

  if (policy.version !== 2) {
    return { version: 2, youtube: readRule(policy, 'youtube'), lossy: keepRule(), lossless: keepRule() };
  }

  return {
    version: 2,
    youtube: readRule(policy.youtube, 'youtube'),
    lossy: readRule(policy.lossy, 'lossy'),
    lossless: readRule(policy.lossless, 'lossless'),
  };
}

/** One rule as the library's save sends it: every key spelled out. */
function writeRule(rule: OutputRule): OutputRule {
  return {
    codec: rule.codec,
    mode: rule.mode,
    // The API reads these two as numbers (`OutputPolicy.Parse`), so a rule that does not use them
    // carries the defaults a kept file ignores rather than the form's "keep".
    bitrateKbps: rule.bitrateKbps === 'keep' ? 256 : rule.bitrateKbps,
    vbrQuality: rule.vbrQuality === 'keep' ? 0 : rule.vbrQuality,
    sampleRate: rule.sampleRate,
    opusContainer: rule.opusContainer,
  };
}

/** The `outputPolicy` object the library's save sends: version 2, one rule per source class. */
export function writeOutputPolicy(policy: OutputPolicy): OutputPolicy {
  return {
    version: 2,
    youtube: writeRule(policy.youtube),
    lossy: writeRule(policy.lossy),
    lossless: writeRule(policy.lossless),
  };
}

/** The layout presets, in the order the Settings form offers them. */
export const LIBRARY_LAYOUTS: { value: LibraryLayoutName; label: string }[] = [
  { value: 'flat', label: 'Flat' },
  { value: 'artist', label: 'Artist' },
  { value: 'artistAlbum', label: 'Artist → Album' },
  { value: 'plexamp', label: 'Plexamp' },
];

/** The album policies with the one-line descriptions from LIBRARY_OUTPUT §7.3. */
export const ALBUM_POLICIES: { value: AlbumPolicyName; label: string; description: string }[] = [
  {
    value: 'fewestAlbums',
    label: 'Fewest albums',
    description: 'the fewest real albums per artist, the rest in a Singles album',
  },
  {
    value: 'singlesOnly',
    label: 'Singles only',
    description: 'one Singles album per artist — the fewest folders, no real album identity',
  },
  {
    value: 'originalAlbum',
    label: 'Original album',
    description: 'the earliest official album or EP each song appears on',
  },
  {
    value: 'singleRelease',
    label: 'Single release',
    description: "the song's own MusicBrainz single, one folder per song",
  },
  {
    value: 'compilation',
    label: 'Compilation',
    description: 'everything under one Various Artists compilation',
  },
];

/**
 * Reads an enum the document types as a number but the API sends as a camelCase string. The cast is
 * the one place those two disagree; the value is always one of the names above, because the server
 * serialises the enum with `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.
 */
export function readEnum<TName extends string>(value: unknown): TName {
  return value as TName;
}

/** A failed write whose body carried an RFC 7807 problem, with its per-field messages when it had any. */
export class ValidationError extends ApiError {
  /** The field messages the problem carried, keyed by the resource member the server named. */
  readonly fields: Record<string, string>;

  constructor(status: number, message: string, fields: Record<string, string>) {
    super(status, message);
    this.name = 'ValidationError';
    this.fields = fields;
  }
}

/**
 * Turns an RFC 7807 validation body's `errors` member — `{ field: [messages] }` — into the single
 * message per field a form input takes. A body with no `errors` reads as no field messages.
 */
export function validationFields(body: unknown): Record<string, string> {
  if (typeof body !== 'object' || body === null) {
    return {};
  }

  const errors = (body as Record<string, unknown>).errors;

  if (typeof errors !== 'object' || errors === null) {
    return {};
  }

  const fields: Record<string, string> = {};

  for (const [field, messages] of Object.entries(errors)) {
    const list: unknown[] = Array.isArray(messages) ? messages : [messages];
    const first: unknown = list[0];

    if (typeof first === 'string' && first !== '') {
      fields[field] = first;
    }
  }

  return fields;
}

/** Reads a problem member the API may have left out. */
function problemText(body: unknown, member: 'detail' | 'title'): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const value = (body as Record<string, unknown>)[member];

  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * Turns a failed response into the error a form renders. The body is the one the client has already
 * parsed: the response's own body is consumed by then, so re-reading it would lose the problem.
 */
function readProblem(status: number, body: unknown, fallback: string): ValidationError {
  const message = problemText(body, 'detail') ?? problemText(body, 'title') ?? fallback;

  return new ValidationError(status, message, validationFields(body));
}

/** The quality ladder every profile and the Wanted lists name their qualities from. */
export function useQualityDefinitions(): UseQueryResult<QualityDefinitionResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: QUALITY_DEFINITIONS_QUERY_KEY,
    queryFn: async (): Promise<QualityDefinitionResource[]> => {
      const { data, response } = await client.GET('/api/v1/qualitydefinition');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The quality definitions request failed.');
      }

      return data;
    },
  });
}

/** Every quality profile. */
export function useQualityProfiles(): UseQueryResult<QualityProfileResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: QUALITY_PROFILES_QUERY_KEY,
    queryFn: async (): Promise<QualityProfileResource[]> => {
      const { data, response } = await client.GET('/api/v1/qualityprofile');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The quality profiles request failed.');
      }

      return data;
    },
  });
}

/** Creates a profile when its `id` is 0, replaces it otherwise. */
export function useSaveQualityProfile(): UseMutationResult<QualityProfileResource, Error, QualityProfileResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (profile: QualityProfileResource): Promise<QualityProfileResource> => {
      const result =
        profile.id === 0
          ? await client.POST('/api/v1/qualityprofile', { body: profile })
          : await client.PUT('/api/v1/qualityprofile/{id}', {
              params: { path: { id: Number(profile.id) } },
              body: profile,
            });

      if (!result.response.ok || result.data === undefined) {
        throw readProblem(result.response.status, result.error, 'The quality profile could not be saved.');
      }

      return result.data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUALITY_PROFILES_QUERY_KEY }),
  });
}

/** Deletes a profile; the API answers 409 while a song still uses it. */
export function useDeleteQualityProfile(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/qualityprofile/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw readProblem(response.status, error, 'The quality profile could not be deleted.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUALITY_PROFILES_QUERY_KEY }),
  });
}

/** Every library. Libraries are seeded and edited, never created here. */
export function useLibraries(): UseQueryResult<LibraryResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: LIBRARIES_QUERY_KEY,
    queryFn: async (): Promise<LibraryResource[]> => {
      const { data, response } = await client.GET('/api/v1/library');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The libraries request failed.');
      }

      return data;
    },
  });
}

/** Creates a library; the API answers 400 with the field messages when the values do not fit. */
export function useCreateLibrary(): UseMutationResult<LibraryResource, Error, LibraryResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (library: LibraryResource): Promise<LibraryResource> => {
      const { data, error, response } = await client.POST('/api/v1/library', { body: library });

      if (!response.ok || data === undefined) {
        throw readProblem(response.status, error, 'The library could not be created.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: LIBRARIES_QUERY_KEY }),
  });
}

/** Deletes a library that holds nothing; the API answers 400 naming what is still in it. */
export function useDeleteLibrary(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/library/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw readProblem(response.status, error, 'The library could not be deleted.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: LIBRARIES_QUERY_KEY }),
  });
}

/** Replaces a library's settings. */
export function useSaveLibrary(): UseMutationResult<LibraryResource, Error, LibraryResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (library: LibraryResource): Promise<LibraryResource> => {
      const { data, error, response } = await client.PUT('/api/v1/library/{id}', {
        params: { path: { id: Number(library.id) } },
        body: library,
      });

      if (!response.ok || data === undefined) {
        throw readProblem(response.status, error, 'The library could not be saved.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: LIBRARIES_QUERY_KEY }),
  });
}

/**
 * Renders a naming template against the library's sample song, for the live preview under the
 * template field. It is a POST because the template being previewed is not the stored one; nothing
 * is written and no query is invalidated.
 *
 * A template the server cannot render comes back 200 with its `errors` filled in and `path` null, so
 * the caller reads those rather than catching: only a transport or template-independent failure throws.
 */
export function useNamingPreview(): UseMutationResult<LibraryPreviewResource, Error, { id: number; template: string }> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async ({ id, template }: { id: number; template: string }): Promise<LibraryPreviewResource> => {
      const { data, error, response } = await client.POST('/api/v1/library/{id}/preview', {
        params: { path: { id } },
        body: { songId: null, template },
      });

      if (!response.ok || data === undefined) {
        throw readProblem(response.status, error, 'The template could not be previewed.');
      }

      return data;
    },
  });
}
