/** A failed API call. Carries the status so pages can tell "not ready yet" from "broken". */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/**
 * A count from a response (`int64` arrives as `number | string`), or an `ApiError` when it is not a number.
 * A query must never cache `NaN`: `NaN !== NaN`, so React Query sees a new result on every render and loops.
 */
export function countOrThrow(value: unknown, status: number, message: string): number {
  const count = typeof value === 'number' || typeof value === 'string' ? Number(value) : Number.NaN;

  if (!Number.isFinite(count)) {
    throw new ApiError(status, message);
  }

  return count;
}
