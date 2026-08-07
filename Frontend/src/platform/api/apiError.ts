import { isAxiosError } from 'axios';

/**
 * The reason a platform call failed, in the words the server used.
 *
 * <b>What this replaces.</b> The console used to read exactly one shape — the hand-written
 * `{ error: "…" }` that the guard rails return — and fall back to a generic line for
 * everything else. Everything else includes the single most common failure in the product:
 * a DataAnnotations refusal, which ASP.NET returns as `ValidationProblemDetails`
 * (`{ errors: { Field: ["…"] } }`) and never as `error`. So a request rejected for a bad
 * field surfaced as "Provisioning was not accepted" — the operator is told the thing they
 * already know, and the one piece of information they need is on the wire and thrown away.
 *
 * It also could not tell a refusal from an outage: a 404 against an API that has not been
 * deployed yet, and a CORS or network failure where no response exists at all, both rendered
 * as the same sentence as a validation error. Those need different actions from the operator,
 * so they now read differently.
 *
 * Ordered most specific first, because a server that sends both a curated message and a field
 * list means the curated message.
 */
export const platformErrorMessage = (error: unknown, fallback: string): string => {
  if (!isAxiosError(error)) return fallback;

  // No response at all: the request never reached the API or the browser refused to hand the
  // answer over. Naming it stops an operator retyping a form that was never the problem.
  if (!error.response) {
    return error.code === 'ECONNABORTED'
      ? `${fallback}: the server did not answer in time. It may still have accepted the request — check before retrying.`
      : `${fallback}: could not reach the server. Check your connection, then confirm the API is running.`;
  }

  const { status, data } = error.response;

  // A curated guard-rail message. Deliberately first: when a server sends one, it has decided
  // what the operator should be told.
  const curated = readString(data, 'error') ?? readString(data, 'detail');
  if (curated) return curated;

  // ValidationProblemDetails. Field names arrive in the server's casing and dotted for nested
  // objects ("Tenant.AdminEmail"); the leaf is the part an operator can act on.
  const fieldErrors = readFieldErrors(data);
  if (fieldErrors.length > 0) {
    const detail = fieldErrors
      .map(([field, messages]) => (field ? `${humanise(field)}: ${messages.join(' ')}` : messages.join(' ')))
      .join(' · ');
    return `${fallback} — ${detail}`;
  }

  const title = readString(data, 'title');
  if (title) return `${fallback}: ${title}`;

  // A bare string body (ASP.NET returns one from `BadRequest("…")`).
  if (typeof data === 'string' && data.trim().length > 0 && !data.trimStart().startsWith('<'))
    return data.trim();

  // Nothing usable in the body. The status still distinguishes an outage from a refusal, and
  // 404 in particular means the endpoint is absent — an API older than this console, which is
  // a deployment problem and not something the operator can fix by editing the form.
  return `${fallback}: ${describeStatus(status)}`;
};

const readString = (data: unknown, key: string): string | null => {
  if (!data || typeof data !== 'object') return null;
  const value = (data as Record<string, unknown>)[key];
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
};

/**
 * Both shapes ASP.NET produces: `ValidationProblemDetails` nests the map under `errors`, and
 * `BadRequest(ModelState)` serialises the map at the top level.
 */
const readFieldErrors = (data: unknown): [string, string[]][] => {
  if (!data || typeof data !== 'object') return [];
  const record = data as Record<string, unknown>;
  const map = (record.errors && typeof record.errors === 'object' ? record.errors : record) as Record<string, unknown>;

  return Object.entries(map)
    .filter(([key]) => !PROBLEM_DETAILS_KEYS.has(key))
    .map(([key, value]): [string, string[]] => [
      key,
      Array.isArray(value)
        ? value.filter((entry): entry is string => typeof entry === 'string')
        : typeof value === 'string'
          ? [value]
          : [],
    ])
    .filter(([, messages]) => messages.length > 0);
};

/** The envelope's own fields, which are never field names. */
const PROBLEM_DETAILS_KEYS = new Set(['type', 'title', 'status', 'detail', 'instance', 'traceId', 'errors']);

/**
 * "Tenant.AdminEmail" → "Admin email". The operator is looking at a form, not at the DTO,
 * so the path prefix and the casing are noise.
 */
const humanise = (field: string): string => {
  const leaf = field.split('.').pop() ?? field;
  if (leaf === '' || leaf === '$') return '';
  const spaced = leaf.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
};

const describeStatus = (status: number): string => {
  switch (status) {
    case 400: return 'the server rejected the request as malformed.';
    case 401: return 'your session is no longer valid. Sign in again.';
    case 403: return 'your role does not carry the authority for this action.';
    case 404:
      // Worth spelling out. The console and the API deploy separately, so this is the exact
      // symptom of a front end that is newer than the service behind it.
      return 'that endpoint does not exist on the server. The API is likely older than this console — check that the backend has finished deploying.';
    case 409: return 'this conflicts with the current state. Reload and try again.';
    case 429: return 'too many requests. Wait a moment and retry.';
    case 502:
    case 503:
    case 504: return 'the server is unavailable or still starting up.';
    default: return status >= 500 ? `the server failed (HTTP ${status}).` : `HTTP ${status}.`;
  }
};
