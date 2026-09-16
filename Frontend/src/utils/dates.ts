// Shared date helpers: the ONE place an API timestamp becomes words on a screen.
//
// Two traps live here, and every screen that formatted a date by hand fell into one of them:
//
//   1. The server stores `timestamp without time zone` and serialises it with no offset
//      ("2026-09-15T20:19:42.1"). `new Date(...)` reads an offset-less ISO string as LOCAL time,
//      so a file uploaded seconds ago in Riyadh (UTC+3) read as three hours in the future —
//      "Received: in 4 hours" — and a quote created late on the 15th dated itself the 16th.
//      Every offset-less value from the API is UTC. `normalizeApiDate` says so by appending "Z".
//
//   2. Backend DTOs sometimes carry DateTime.MinValue ("0001-01-01T00:00:00") when a date was
//      never captured. Rendering that as "01 Jan 1" is a data leak. Anything before
//      MIN_VALID_YEAR is "not set".
//
// One style everywhere: "15 Sep 2026" for a day, "15 Sep 2026, 23:19" for an instant, both in
// the reader's own zone, never with a zone name appended. Relative phrases for a RECEIVED date
// are clamped to "just now": a document cannot have arrived in the future, and a few seconds of
// clock skew between the server and the browser must not say it did.

export const MIN_VALID_YEAR = 2000;

/** "2026-09-15" — a calendar day with no time. Rendered as that day wherever the reader is. */
const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;
/** Ends in Z or an explicit ±HH:MM / ±HHMM offset. */
const HAS_ZONE = /(?:Z|[+-]\d{2}:?\d{2})$/i;
/** Looks like an ISO date-time: YYYY-MM-DD then a T (or a space) then a time. */
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/;

/**
 * The API's timestamp as an unambiguous ISO string. An offset-less date-time is UTC and gets a
 * "Z"; a value that already states its zone is returned untouched; a bare calendar day is
 * returned untouched (JavaScript already reads it as UTC midnight, and the formatters below keep
 * it on that day). Anything else is passed through for `Date` to judge.
 */
export function normalizeApiDate(value: string): string {
  const trimmed = value.trim();
  if (DATE_ONLY.test(trimmed) || HAS_ZONE.test(trimmed) || !ISO_DATE_TIME.test(trimmed)) return trimmed;
  return `${trimmed.replace(' ', 'T')}Z`;
}

/**
 * Parses a date string defensively. Returns null for null/blank input, unparseable values, and
 * sentinel dates (anything before MIN_VALID_YEAR, which catches DateTime.MinValue and other
 * placeholder values). Offset-less API values are read as UTC.
 */
export function parseDateSafe(dateStr: string | null | undefined): Date | null {
  if (!dateStr) return null;
  const d = new Date(normalizeApiDate(dateStr));
  if (Number.isNaN(d.getTime())) return null;
  if (d.getUTCFullYear() < MIN_VALID_YEAR) return null;
  return d;
}

// Spelled out rather than asked of Intl: recent ICU data renders en-GB September as "Sept", so
// the same instant would read "15 Sept" on one browser and "15 Sep" on another.
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;
const two = (n: number) => String(n).padStart(2, '0');

/** Calendar fields of an instant — in the reader's zone, or in UTC for a bare calendar day. */
const fields = (d: Date, utc: boolean) => utc
  ? { day: d.getUTCDate(), month: d.getUTCMonth(), year: d.getUTCFullYear(), hours: d.getUTCHours(), minutes: d.getUTCMinutes() }
  : { day: d.getDate(), month: d.getMonth(), year: d.getFullYear(), hours: d.getHours(), minutes: d.getMinutes() };

/**
 * Formats a date as "15 Sep 2026". Sentinel/missing/invalid dates render as the fallback (an em
 * dash by default) so users see "not set" rather than "01 Jan 1". A bare calendar day is kept on
 * that day for every reader, wherever they are.
 */
export function formatDateSafe(dateStr: string | null | undefined, fallback = '—'): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  const f = fields(d, DATE_ONLY.test(dateStr!.trim()));
  return `${two(f.day)} ${MONTHS[f.month]} ${f.year}`;
}

/**
 * Formats an instant as "15 Sep 2026, 23:19" in the reader's zone — the one date-time style this
 * product uses. A bare calendar day has no time and renders as the day alone.
 */
export function formatDateTime(dateStr: string | null | undefined, fallback = '—'): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  if (DATE_ONLY.test(dateStr!.trim())) return formatDateSafe(dateStr, fallback);
  const f = fields(d, false);
  return `${two(f.day)} ${MONTHS[f.month]} ${f.year}, ${two(f.hours)}:${two(f.minutes)}`;
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY_MS = 24 * HOUR;

/**
 * How long ago something was received, in the reader's words: "just now", "5 minutes ago",
 * "3 hours ago", "2 days ago", then the date itself. NEVER "in 4 hours": a received date in the
 * future is clock skew or a zone mistake, and both read as "just now".
 */
export function formatRelativeReceived(
  dateStr: string | null | undefined,
  now: Date = new Date(),
  fallback = '—',
): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  const elapsed = now.getTime() - d.getTime();
  if (elapsed < 45_000) return 'just now';
  if (elapsed < HOUR) {
    const minutes = Math.max(1, Math.round(elapsed / MINUTE));
    return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
  }
  if (elapsed < DAY_MS) {
    const hours = Math.round(elapsed / HOUR);
    return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  }
  if (elapsed < 7 * DAY_MS) {
    const days = Math.round(elapsed / DAY_MS);
    return `${days} day${days === 1 ? '' : 's'} ago`;
  }
  return formatDateSafe(dateStr, fallback);
}

/**
 * Formats a moment as "15 Sep 2026, 23:19": the date exactly as every list shows it, then the
 * clock in 24-hour form. One shape for every timestamp a person reads, so a time on one screen
 * never has to be translated into the date format of the next.
 */
export function formatDateTimeSafe(dateStr: string | null | undefined, fallback = '—'): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  const date = d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false });
  return `${date}, ${time}`;
}
