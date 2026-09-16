import { describe, expect, it } from 'vitest';
import { formatDateSafe, formatDateTimeSafe } from './dates';

/**
 * Timestamps were rendered with `toLocaleString()` — "9/15/2026, 11:19:22 PM" on one screen while
 * the next showed "15 Sep 2026". One shape everywhere: the date the lists already use, then the
 * clock.
 */
describe('formatDateTimeSafe', () => {
  it('prints the list date format followed by a 24-hour clock', () => {
    const iso = '2026-09-15T20:19:22Z';
    const text = formatDateTimeSafe(iso);
    // Month words are the locale's own — en-GB says "Sept", so two to four letters.
    expect(text).toMatch(/^\d{2} [A-Z][a-z]{2,4} \d{4}, \d{2}:\d{2}$/);
    // Same date words as the rest of the product, then the time in the reader's own zone.
    expect(text.startsWith(`${formatDateSafe(iso)}, `)).toBe(true);
    const local = new Date(iso);
    const hh = String(local.getHours()).padStart(2, '0');
    const mm = String(local.getMinutes()).padStart(2, '0');
    expect(text.endsWith(`${hh}:${mm}`)).toBe(true);
    expect(text).not.toMatch(/AM|PM|\d\/\d/);
  });

  it('renders a missing or sentinel moment as the fallback, never as year 1', () => {
    expect(formatDateTimeSafe(null)).toBe('—');
    expect(formatDateTimeSafe('')).toBe('—');
    expect(formatDateTimeSafe('0001-01-01T00:00:00')).toBe('—');
    expect(formatDateTimeSafe('not a date', 'Not recorded')).toBe('Not recorded');
  });
});
