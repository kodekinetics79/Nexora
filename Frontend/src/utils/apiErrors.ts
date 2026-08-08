/**
 * Error-presentation boundary.
 *
 * Every user-visible failure message in the product must come through here. The rules this file
 * enforces exist because raw backend payloads were being rendered directly into snackbars:
 *
 *  1. A `response.data` value that is not a string is NEVER rendered. An object renders as
 *     `[object Object]`; an HTML 502 page renders as raw markup.
 *  2. An axios/`Error` `.message` is NEVER surfaced to a user. It leaks the API hostname
 *     ("Request failed ... https://api.internal.example"), transport codes and stack context.
 *  3. A server string that *looks* like operator diagnostics (exception type names, stack frames,
 *     host:port, SQL, markup) is demoted to the support disclosure rather than shown as product
 *     copy. This is the "ClamAV is unavailable (SocketException)" class of leak.
 *  4. Anything unrecognised falls back to a safe generic message, with the original detail kept in
 *     `technicalDetail` for the copyable "Technical detail (for support)" disclosure.
 *  5. A 4xx whose body is a PLAIN STRING (`text/plain`, or a JSON-encoded string the transport left
 *     quoted) IS rendered verbatim when it is safe printable text: bounded length, no markup, no
 *     stack/host markers, no control characters, and not a bare HTTP reason phrase ("Bad Request").
 *     Backends in this product return honest one-line reasons that way ("A tenant-owned lead is
 *     required so the RFQ belongs to a commercial case."); swallowing them hid the real fix from
 *     users. Only genuinely unrenderable bodies fall back to the generic wording.
 *  6. RFC 7807 ProblemDetails: `detail` is preferred as the user-facing message (subject to the
 *     same safety gate), and a `traceId` is always carried into the support disclosure — even when
 *     the detail was rendered — so support can correlate the report with server logs.
 *
 * 401 handling deliberately mirrors `src/api/axiosInstance.ts`: the response interceptor there owns
 * the redirect to /login. This module only produces the wording, and never triggers navigation.
 */

export type ApiErrorSeverity = 'error' | 'warning' | 'info';

export interface PresentableError {
  /** Short heading, safe for a dialog title or Alert title. */
  title: string;
  /** User-facing sentence. Always plain product copy — never backend diagnostics. */
  message: string;
  severity: ApiErrorSeverity;
  /** True when the same action is worth attempting again without the user changing anything. */
  isRetryable: boolean;
  /**
   * Operator detail for the copyable "Technical detail (for support)" disclosure. Undefined when
   * there is nothing beyond what `message` already says.
   */
  technicalDetail?: string;
  /** HTTP status when the request reached the server. */
  status?: number;
  /** True when the request never produced a response (offline, DNS, CORS, timeout). */
  isNetworkFailure: boolean;
  /** True when the caller aborted the request; usually not worth showing at all. */
  isCanceled: boolean;
}

const GENERIC_MESSAGE = 'Something went wrong on our side. Your work is safe and nothing was lost.';
const MAX_MESSAGE_LENGTH = 300;
const MAX_TECHNICAL_LENGTH = 2000;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const trimmedString = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
};

/**
 * A `text/plain` (or mis-labelled) body can arrive still JSON-encoded: `'"A lead is required."'`.
 * Axios usually unwraps it, but not for every content-type/adapter combination. One level of
 * unwrap keeps the quotes out of product copy; anything that parses to a non-string stays as-is.
 */
const unwrapJsonStringLiteral = (value: string): string => {
  if (value.length < 2 || !value.startsWith('"') || !value.endsWith('"')) return value;
  try {
    const parsed: unknown = JSON.parse(value);
    return typeof parsed === 'string' ? parsed : value;
  } catch {
    return value;
  }
};

/** Snackbars are one line; collapse newlines/runs of whitespace before rendering server text. */
const collapseWhitespace = (value: string): string => value.replace(/\s+/g, ' ').trim();

/** C0/C1 control characters (tab/newline/CR excluded — `collapseWhitespace` removes those). */
const containsNonPrintable = (value: string): boolean => Array.from(value).some((character) => {
  const codePoint = character.codePointAt(0) ?? 0;
  return codePoint <= 0x08
    || codePoint === 0x0b
    || codePoint === 0x0c
    || (codePoint >= 0x0e && codePoint <= 0x1f)
    || (codePoint >= 0x7f && codePoint <= 0x9f);
});

/**
 * Status-line reason phrases and framework defaults ("Bad Request", ProblemDetails' stock titles)
 * carry no information the status-derived copy doesn't already say better. They are not rendered,
 * so a ProblemDetails `title` never displaces real product copy when `detail` is absent.
 */
const GENERIC_REASON_PHRASES = new Set([
  'bad request',
  'unauthorized',
  'forbidden',
  'not found',
  'conflict',
  'unprocessable entity',
  'internal server error',
  'service unavailable',
  'error',
  'an error occurred',
  'an error occurred while processing your request',
  'one or more validation errors occurred',
]);

const isGenericReasonPhrase = (value: string): boolean =>
  GENERIC_REASON_PHRASES.has(value.trim().replace(/[.!]+$/, '').toLowerCase());

/** ASP.NET stamps ProblemDetails with a `traceId` extension; keep it for support correlation. */
const problemTraceId = (data: unknown): string | null =>
  isRecord(data) ? trimmedString(data.traceId) : null;

/**
 * Heuristic gate for server-supplied strings. A backend `message`/`detail` is only product copy if
 * it reads like a sentence written for a user. Anything carrying operator signal — exception type
 * names, stack frames, host:port, file paths, markup, SQL — is diagnostics wearing a message field.
 */
const TECHNICAL_MARKERS: RegExp[] = [
  /<\s*(!doctype|html|head|body|div|span|pre|title)\b/i, // HTML error pages
  /\b\w*Exception\b/, // SocketException, HttpRequestException, ...
  /\b(?:System|Microsoft|java|org\.apache|nginx|Npgsql)\./, // namespaced type names
  /(?:^|\n)\s*at\s+\S+/, // stack frames
  /\bStackTrace\b|\bInnerException\b/i,
  /\b[a-z0-9.-]+:\d{2,5}\b/i, // host:port
  /\bhttps?:\/\/\S+/i, // URLs / hostnames
  /[A-Za-z]:\\|\/(?:var|usr|home|opt|etc)\//, // filesystem paths
  /\b(?:SELECT|INSERT|UPDATE|DELETE)\b\s+.*\b(?:FROM|INTO|SET)\b/i, // SQL
  /\b(?:ECONNREFUSED|ETIMEDOUT|ENOTFOUND|EPIPE|ERR_[A-Z_]+)\b/, // transport codes
  /\b(?:errno|SQLSTATE|HRESULT)\b/i,
];

export const looksLikeTechnicalNoise = (value: string): boolean =>
  TECHNICAL_MARKERS.some((marker) => marker.test(value));

/**
 * A server string is shown only when it is a plausible sentence and free of operator signal:
 * non-empty, bounded, printable, not a bare reason phrase, and free of technical markers.
 */
const isPresentableServerText = (value: string): boolean =>
  value.length > 0 &&
  value.length <= MAX_MESSAGE_LENGTH &&
  !containsNonPrintable(value) &&
  !isGenericReasonPhrase(value) &&
  !looksLikeTechnicalNoise(value);

const truncate = (value: string, limit: number): string =>
  value.length <= limit ? value : `${value.slice(0, limit - 1)}…`;

/** Serialises anything into support-disclosure text without ever letting it reach `message`. */
const describeForSupport = (value: unknown): string | null => {
  if (value === null || value === undefined) return null;
  if (typeof value === 'string') return trimmedString(value);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  try {
    const serialised = JSON.stringify(value);
    // `JSON.stringify` yields undefined for functions/symbols and "{}" for empty or non-enumerable
    // objects; neither is worth showing.
    if (!serialised || serialised === '{}' || serialised === '[]') return null;
    return serialised;
  } catch {
    return null; // circular structure
  }
};

/**
 * ASP.NET ProblemDetails validation payloads: `{ errors: { field: ["msg", ...] } }`. These strings
 * are authored for users, so they are the one server-supplied shape rendered close to verbatim.
 */
const validationMessage = (data: unknown): string | null => {
  if (!isRecord(data) || !isRecord(data.errors)) return null;
  const lines: string[] = [];
  for (const [field, messages] of Object.entries(data.errors)) {
    const list = Array.isArray(messages) ? messages : [messages];
    for (const entry of list) {
      const text = trimmedString(entry);
      if (!text || !isPresentableServerText(text)) continue;
      // A field key of "" is ProblemDetails' bucket for non-field-specific errors.
      lines.push(field ? `${field}: ${text}` : text);
    }
  }
  if (lines.length === 0) return null;
  return truncate(lines.join(' '), MAX_MESSAGE_LENGTH);
};

/**
 * Pulls the first PRESENTABLE string candidate out of a response body. Non-strings are ignored.
 * `detail` (RFC 7807) outranks `title`, so a ProblemDetails body renders its specific sentence and
 * a bland stock title never shadows it; keys that fail the safety gate are skipped rather than
 * poisoning the whole body, so `{ title: "Bad Request", detail: "A lead is required." }` still
 * renders the detail.
 */
const SERVER_TEXT_KEYS = ['message', 'detail', 'error', 'title', 'errorMessage'] as const;

/**
 * Gates on the RAW string first (the stack-frame marker is line-anchored), then collapses
 * whitespace so multi-line server text renders as one snackbar-safe sentence.
 */
const presentableCandidate = (raw: string): string | null => {
  if (!isPresentableServerText(raw)) return null;
  const collapsed = collapseWhitespace(raw);
  return collapsed.length > 0 ? collapsed : null;
};

/**
 * THE gate for rendering a server-supplied sentence as product copy, exported so nothing in the
 * product grows a second, weaker copy of these rules.
 *
 * Returns the sentence when it is safe to show — a string (never an object, never an axios
 * `.message`), bounded to {@link MAX_MESSAGE_LENGTH}, printable, not a bare HTTP reason phrase and
 * free of the operator markers in {@link TECHNICAL_MARKERS} (hostnames, URLs, exception type names,
 * stack frames, SQL, paths) — and `null` when it must not be shown.
 *
 * Used by the intake pipeline (src/utils/intakeErrors.ts) as well as by `toPresentableError`: the
 * reason a document was rejected is server-authored text and gets exactly the same scrutiny as a
 * response body, whether it arrives in a 4xx or on a 202 batch row.
 */
export const presentableServerText = (value: unknown): string | null => {
  const trimmed = trimmedString(value);
  if (trimmed === null) return null;
  return presentableCandidate(unwrapJsonStringLiteral(trimmed));
};

const serverText = (data: unknown): string | null => {
  const direct = trimmedString(data);
  if (direct !== null) {
    // A bare-string body ("A tenant-owned lead is required …") is the message, modulo one level
    // of JSON quoting some transports leave in place.
    return presentableCandidate(unwrapJsonStringLiteral(direct));
  }
  if (!isRecord(data)) return null;
  for (const key of SERVER_TEXT_KEYS) {
    const candidate = trimmedString(data[key]);
    if (candidate === null) continue;
    const presentable = presentableCandidate(candidate);
    if (presentable !== null) return presentable;
  }
  return null;
};

interface StatusCopy {
  title: string;
  message: string;
  severity: ApiErrorSeverity;
  isRetryable: boolean;
  /** When false, a server-supplied string must not replace `message` (auth/permission wording). */
  allowServerText: boolean;
}

const statusCopy = (status: number): StatusCopy => {
  if (status === 400) {
    return {
      title: 'That request could not be accepted',
      message: 'Some of the submitted details were not valid. Review the highlighted fields and try again.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 401) {
    return {
      title: 'Your session has ended',
      message: 'Sign in again to continue. Nothing you submitted was lost.',
      severity: 'error',
      isRetryable: false,
      allowServerText: false,
    };
  }
  if (status === 403) {
    return {
      title: 'You do not have access to this',
      message: 'Your role does not permit this action. Ask an administrator if you need it.',
      severity: 'error',
      isRetryable: false,
      allowServerText: false,
    };
  }
  if (status === 404) {
    return {
      title: 'Not found',
      message: 'This record no longer exists, or it belongs to another organization.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 409) {
    return {
      title: 'This changed while you were working',
      message: 'Someone else updated this record. Refresh to load the current version, then reapply your change.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 413) {
    return {
      title: 'That upload is too large',
      message: 'The files exceed the size this service accepts. Remove or split the largest files and try again.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 415) {
    return {
      title: 'Unsupported file type',
      message: 'This file format is not accepted here. Convert it to a supported format and try again.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 422) {
    return {
      title: 'That action could not be completed',
      message: 'The request was understood but could not be applied in the current state.',
      severity: 'warning',
      isRetryable: false,
      allowServerText: true,
    };
  }
  if (status === 408 || status === 429) {
    return {
      title: status === 408 ? 'The request timed out' : 'Too many requests',
      message: status === 408
        ? 'The service did not answer in time. Nothing was changed — try again.'
        : 'You have made too many requests in a short period. Wait a moment and try again.',
      severity: 'warning',
      isRetryable: true,
      allowServerText: false,
    };
  }
  if (status === 503 || status === 502 || status === 504) {
    return {
      title: 'The service is temporarily unavailable',
      message: 'This part of Nexora is not responding right now. Your data is safe — try again shortly.',
      severity: 'warning',
      isRetryable: true,
      allowServerText: false,
    };
  }
  if (status >= 500) {
    return {
      title: 'Something went wrong on our side',
      message: GENERIC_MESSAGE,
      severity: 'error',
      isRetryable: true,
      // A 500 body is an unhandled-exception dump far more often than it is product copy.
      allowServerText: false,
    };
  }
  return {
    title: 'That action could not be completed',
    message: GENERIC_MESSAGE,
    severity: 'error',
    isRetryable: false,
    allowServerText: true,
  };
};

interface AxiosLikeShape {
  status?: number;
  data?: unknown;
  code?: string;
  errorMessage?: string;
  method?: string;
  url?: string;
  hadRequest: boolean;
}

/** Reads the axios error shape structurally, so this module never imports axios itself. */
const readAxiosLike = (error: unknown): AxiosLikeShape => {
  if (!isRecord(error)) {
    return { hadRequest: false };
  }
  const response = isRecord(error.response) ? error.response : undefined;
  const config = isRecord(error.config) ? error.config : undefined;
  return {
    status: typeof response?.status === 'number' ? response.status : undefined,
    data: response?.data,
    code: trimmedString(error.code) ?? undefined,
    errorMessage: trimmedString(error.message) ?? undefined,
    method: trimmedString(config?.method)?.toUpperCase() ?? undefined,
    url: trimmedString(config?.url) ?? undefined,
    hadRequest: 'request' in error && error.request !== undefined,
  };
};

/** Strips scheme+host so the support disclosure never advertises the API hostname. */
const requestPath = (url: string | undefined): string | undefined => {
  if (!url) return undefined;
  const withoutOrigin = url.replace(/^[a-z][a-z0-9+.-]*:\/\/[^/]+/i, '');
  const withoutQuery = withoutOrigin.split('?')[0];
  return withoutQuery || undefined;
};

/**
 * Redacts any origin embedded in free text. `requestPath` only cleans `config.url`; axios also
 * bakes the full URL into `error.message` ("Request failed ... at https://api.internal…"), and
 * server bodies quote endpoints too. The support disclosure keeps the diagnostic value without
 * publishing where the API lives.
 */
const redactOrigins = (value: string): string =>
  value.replace(/\b[a-z][a-z0-9+.-]*:\/\/[^\s/]+/gi, '[host]');

const buildTechnicalDetail = (
  shape: AxiosLikeShape,
  demotedServerText: string | null,
  traceId?: string | null,
): string | undefined => {
  const parts: string[] = [];
  if (shape.status !== undefined) parts.push(`HTTP ${shape.status}`);
  if (shape.code) parts.push(`code=${shape.code}`);
  const path = requestPath(shape.url);
  if (path) parts.push(`${shape.method ?? 'REQUEST'} ${path}`);
  if (shape.errorMessage) parts.push(`error=${redactOrigins(shape.errorMessage)}`);
  if (demotedServerText) parts.push(`body=${redactOrigins(demotedServerText)}`);
  // The ProblemDetails traceId is always disclosed — even when the body's `detail` became the
  // user-facing message — so a support report can be matched to the server-side log line.
  if (traceId) parts.push(`traceId=${traceId}`);
  if (parts.length === 0) return undefined;
  return truncate(parts.join(' | '), MAX_TECHNICAL_LENGTH);
};

/**
 * Converts any thrown value into presentation-ready copy.
 *
 * @param error   The unknown value from a catch block, `onError` handler or react-query `error`.
 * @param options `fallbackMessage` replaces the generic wording when the caller has a better
 *                domain-specific sentence; it is used only when the server supplies nothing safe.
 */
export const toPresentableError = (
  error: unknown,
  options?: { fallbackMessage?: string; fallbackTitle?: string },
): PresentableError => {
  const shape = readAxiosLike(error);
  const isCanceled =
    shape.code === 'ERR_CANCELED' ||
    (isRecord(error) && error.name === 'CanceledError') ||
    (isRecord(error) && error.name === 'AbortError');

  if (isCanceled) {
    return {
      title: 'Request cancelled',
      message: 'That request was cancelled before it finished.',
      severity: 'info',
      isRetryable: true,
      status: undefined,
      isNetworkFailure: false,
      isCanceled: true,
      technicalDetail: buildTechnicalDetail(shape, null),
    };
  }

  // No status means the request never came back: offline, DNS failure, CORS, or a timeout.
  if (shape.status === undefined) {
    const timedOut = shape.code === 'ECONNABORTED' || shape.code === 'ETIMEDOUT';
    const isNetworkFailure = timedOut || shape.code === 'ERR_NETWORK' || shape.hadRequest;
    if (isNetworkFailure) {
      return {
        title: timedOut ? 'The request timed out' : 'Cannot reach Nexora',
        message: timedOut
          ? 'The service did not answer in time. Nothing was changed — check your connection and try again.'
          : 'Your device could not reach the service. Check your connection and try again; nothing was lost.',
        severity: 'warning',
        isRetryable: true,
        status: undefined,
        isNetworkFailure: true,
        isCanceled: false,
        technicalDetail: buildTechnicalDetail(shape, null),
      };
    }
    // A non-HTTP throw: a bug in our own code, a rejected string, or a plain Error. Its `.message`
    // is diagnostics, so it goes to the disclosure and never to `message`.
    return {
      title: options?.fallbackTitle ?? 'Something went wrong',
      message: options?.fallbackMessage ?? GENERIC_MESSAGE,
      severity: 'error',
      isRetryable: false,
      status: undefined,
      isNetworkFailure: false,
      isCanceled: false,
      technicalDetail: buildTechnicalDetail(shape, null) ?? describeForSupport(error) ?? undefined,
    };
  }

  const copy = statusCopy(shape.status);
  // `validationMessage` and `serverText` only ever return already-gated, presentation-ready text.
  const validation = validationMessage(shape.data);
  const candidate = validation ?? serverText(shape.data);
  const useServerText = copy.allowServerText && candidate !== null;

  // Everything the server said that we chose not to render still reaches support.
  const demoted = useServerText ? null : describeForSupport(shape.data);
  const traceId = problemTraceId(shape.data);

  return {
    title: copy.title,
    message: useServerText
      ? candidate
      : options?.fallbackMessage ?? copy.message,
    severity: copy.severity,
    isRetryable: copy.isRetryable,
    status: shape.status,
    isNetworkFailure: false,
    isCanceled: false,
    technicalDetail: buildTechnicalDetail(shape, demoted, traceId),
  };
};

/** Convenience for snackbars, which take a single string. */
export const presentableErrorMessage = (
  error: unknown,
  fallbackMessage?: string,
): string => toPresentableError(error, { fallbackMessage }).message;

/** The text the support disclosure copies to the clipboard. */
export const supportDetailText = (presented: PresentableError): string =>
  [presented.title, presented.message, presented.technicalDetail]
    .filter((part): part is string => Boolean(part))
    .join('\n');
