// Shared date-safety helpers.
//
// Backend DTOs sometimes carry DateTime.MinValue ("0001-01-01T00:00:00") when a
// date was never captured. Rendering that as "01 Jan 1" (or coloring it as an
// overdue deadline) is a data leak into the UI. Any date before MIN_VALID_YEAR
// is treated as "not set".

export const MIN_VALID_YEAR = 2000;

/**
 * Parses a date string defensively. Returns null for null/blank input,
 * unparseable values, and sentinel dates (anything before MIN_VALID_YEAR,
 * which catches DateTime.MinValue and other placeholder values).
 */
export function parseDateSafe(dateStr: string | null | undefined): Date | null {
  if (!dateStr) return null;
  const d = new Date(dateStr);
  if (Number.isNaN(d.getTime())) return null;
  if (d.getFullYear() < MIN_VALID_YEAR) return null;
  return d;
}

/**
 * Formats a date as "17 Jul 2026". Sentinel/missing/invalid dates render as
 * the fallback (an em dash by default) so users see "not set" rather than
 * "01 Jan 1".
 */
export function formatDateSafe(dateStr: string | null | undefined, fallback = '—'): string {
  const d = parseDateSafe(dateStr);
  if (!d) return fallback;
  return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
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
