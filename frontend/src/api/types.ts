/**
 * Resource shapes the SPA reads that the committed `docs/api/openapi.json` does not describe usefully.
 *
 * `/api/v1/health` is documented but untyped (the controller returns `IActionResult`), so there is
 * nothing for `openapi-typescript` to generate. This interface mirrors the *arr payload the backend
 * emits; delete it once the endpoint is typed in the document and the generated types cover it.
 */

/** The four outcomes of a health check, serialised camelCase by the API. */
export type HealthCheckOutcome = 'ok' | 'notice' | 'warning' | 'error';

/** One health check result, as `GET /api/v1/health` returns it. */
export interface HealthEntry {
  source: string;
  type: HealthCheckOutcome;
  message: string;
  wikiUrl: string | null;
}

/** The body of `POST /api/v1/command`. */
export interface CommandRequest {
  name: string;
}

const OUTCOMES: readonly HealthCheckOutcome[] = ['ok', 'notice', 'warning', 'error'];

/** Narrows an untyped health payload to the entries the UI expects. */
export function asHealthEntries(value: unknown): HealthEntry[] {
  if (!Array.isArray(value)) {
    throw new TypeError('The health endpoint did not return a list.');
  }

  return value.map((entry) => {
    if (typeof entry !== 'object' || entry === null) {
      throw new TypeError('The health endpoint returned a malformed entry.');
    }

    const candidate = entry as Record<string, unknown>;
    const type = typeof candidate.type === 'string' ? candidate.type.toLowerCase() : '';
    const outcome = OUTCOMES.find((known) => known === type);

    if (typeof candidate.source !== 'string' || typeof candidate.message !== 'string' || outcome === undefined) {
      throw new TypeError('The health endpoint returned a malformed entry.');
    }

    return {
      source: candidate.source,
      type: outcome,
      message: candidate.message,
      wikiUrl: typeof candidate.wikiUrl === 'string' ? candidate.wikiUrl : null,
    };
  });
}

/** True when a health entry is not `ok`, i.e. worth showing a badge for. */
export function isProblem(entry: HealthEntry): boolean {
  return entry.type !== 'ok';
}
