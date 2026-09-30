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
/**
 * "2026-10-08T00:00:00" — a calendar day the server stored in a date-time column (a bid's closing
 * day, a required delivery day). Midnight exactly, no zone: it names a day, not an instant. Read
 * as a UTC instant it showed "07 Oct" to every reader west of UTC while the Hijri date beside it
 * said the 8th. A real instant carries seconds, fractions or a zone and is not caught here.
 */
const MIDNIGHT_DAY = /^\d{4}-\d{2}-\d{2}[T ]00:00(?::00(?:\.0+)?)?$/;
const isCalendarDay = (value: string): boolean => DATE_ONLY.test(value) || MIDNIGHT_DAY.test(value);
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
  const f = fields(d, isCalendarDay(dateStr!.trim()));
  return `${two(f.day)} ${MONTHS[f.month]} ${f.year}`;
}

/**
 * Formats an instant as "15 Sep 2026, 23:19" in the reader's zone — the one date-time style this
 * product uses. A bare calendar day has no time and renders as the day alone.
 */
export function formatDateTime(dateStr: string | null | undefined, fallback = '—'): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  if (isCalendarDay(dateStr!.trim())) return formatDateSafe(dateStr, fallback);
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
/** Same style as formatDateTime; kept for the call sites that adopted this name first. */
export function formatDateTimeSafe(dateStr: string | null | undefined, fallback = '—'): string {
  return formatDateTime(dateStr, fallback);
}

// ── Deadlines: the buyer's own clock ─────────────────────────────────────────────────────────
//
// A bid's closing date is stored exactly as the buyer's portal printed it: "Due date 9/6/2026
// 5:00 PM" is kept as 2026-09-06T17:00:00, with no zone. It is a time on the BUYER's clock, not
// an instant this server recorded, so it is shown as written — "6 Sep 2026, 5:00 PM" — for every
// reader. Moved into the reader's zone like a timestamp, an SEC tender closing "9/13/2026 1:45 AM"
// read "12 Sep" in New York, beside a Hijri date and a days-left count for the 13th.

/** "2026-09-06T17:00:00" → the digits as written. Null when the value is not an ISO date. */
const WALL_CLOCK = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/;

interface WallClock { year: number; month: number; day: number; hours: number; minutes: number; hasTime: boolean }

const readWallClock = (value: string | null | undefined): WallClock | null => {
  const match = WALL_CLOCK.exec((value ?? '').trim());
  if (!match) {
    // Not ISO-shaped: let Date judge it, on the reader's clock.
    const d = parseDateSafe(value);
    return d ? { year: d.getFullYear(), month: d.getMonth(), day: d.getDate(), hours: d.getHours(), minutes: d.getMinutes(), hasTime: true } : null;
  }
  const [, y, m, d, hh, mm] = match;
  const year = Number(y);
  if (year < MIN_VALID_YEAR) return null;
  const hours = hh ? Number(hh) : 0;
  const minutes = mm ? Number(mm) : 0;
  return { year, month: Number(m) - 1, day: Number(d), hours, minutes, hasTime: hours !== 0 || minutes !== 0 };
};

/** "5:00 PM" — the way a portal prints a closing time. */
const clock12 = (hours: number, minutes: number): string =>
  `${hours % 12 === 0 ? 12 : hours % 12}:${two(minutes)} ${hours < 12 ? 'AM' : 'PM'}`;

/**
 * A deadline as the buyer wrote it: "6 Sep 2026, 5:00 PM", or "6 Sep 2026" when no time was
 * stated. Never moved into the reader's zone; see above.
 */
export function formatDeadline(dateStr: string | null | undefined, fallback = '—'): string {
  const w = readWallClock(dateStr);
  if (!w) return fallback;
  const day = `${w.day} ${MONTHS[w.month]} ${w.year}`;
  return w.hasTime ? `${day}, ${clock12(w.hours, w.minutes)}` : day;
}

/** "13 Sep 2026" — the deadline's own day, for a narrow list column. */
export function formatDeadlineDate(dateStr: string | null | undefined, fallback = '—'): string {
  const w = readWallClock(dateStr);
  return w ? `${w.day} ${MONTHS[w.month]} ${w.year}` : fallback;
}

/** "8 Sep" — the short form a question offers as a button. */
export function formatDeadlineDay(dateStr: string | null | undefined, fallback = '—'): string {
  const w = readWallClock(dateStr);
  return w ? `${w.day} ${MONTHS[w.month]}` : fallback;
}

/**
 * The company's time zone, chosen when the company was created (from the session's permissions
 * read). A closing date is the buyer's wall-clock time, so its days left are counted on the
 * company's calendar, the same one the server uses. Null: the reader's own calendar.
 */
let companyTimeZone: string | null = null;

export function setCompanyTimeZone(zone: string | null | undefined): void {
  companyTimeZone = zone && zone.trim() ? zone.trim() : null;
}

/** Year, month (0-based) and day of `now` on the company's calendar, else the reader's. */
function todayParts(now: Date, zone: string | null): { year: number; month: number; day: number } {
  if (zone) {
    try {
      const parts = new Intl.DateTimeFormat('en-US', { timeZone: zone, year: 'numeric', month: 'numeric', day: 'numeric' })
        .formatToParts(now);
      const part = (type: string) => Number(parts.find((p) => p.type === type)?.value);
      const year = part('year');
      const month = part('month');
      const day = part('day');
      if (year && month && day) return { year, month: month - 1, day };
    } catch {
      // A zone this browser does not know: fall back to the reader's calendar.
    }
  }
  return { year: now.getFullYear(), month: now.getMonth(), day: now.getDate() };
}

/**
 * Whole calendar days from today (the company's today) to the deadline's own day: 0 on the day it
 * closes, negative once that day has passed, null when there is no real date. The ONE count every
 * screen uses — the Leads list and Decide used to say 9 and 10 for the same bid.
 */
export function calendarDaysUntil(
  dateStr: string | null | undefined,
  now: Date = new Date(),
  zone: string | null = companyTimeZone,
): number | null {
  const w = readWallClock(dateStr);
  if (!w) return null;
  const due = Date.UTC(w.year, w.month, w.day);
  const t = todayParts(now, zone);
  const today = Date.UTC(t.year, t.month, t.day);
  return Math.round((due - today) / DAY_MS);
}
