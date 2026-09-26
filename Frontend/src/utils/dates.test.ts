import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { formatDateSafe, formatDateTime, formatRelativeReceived, normalizeApiDate, parseDateSafe } from './dates';

/**
 * The server stores `timestamp without time zone` and sends it with no offset. The browser
 * must read that as UTC and show it in the reader's own zone, in ONE style. These tests pin the
 * reader to Riyadh (UTC+3), where the defect was seen: a file uploaded seconds ago said
 * "Received: in 4 hours" and a quote created at 23:20 on the 15th was dated the 16th.
 */
// vi.stubEnv assigns the TZ variable, which Node honours at runtime by resetting its zone cache.
beforeAll(() => { vi.stubEnv('TZ', 'Asia/Riyadh'); });
afterAll(() => { vi.unstubAllEnvs(); });

describe('normalizeApiDate — an offset-less API timestamp is UTC', () => {
  it('appends Z to an offset-less date-time and leaves stated zones alone', () => {
    expect(normalizeApiDate('2026-09-15T20:19:42.1')).toBe('2026-09-15T20:19:42.1Z');
    expect(normalizeApiDate('2026-09-15 20:19:42')).toBe('2026-09-15T20:19:42Z');
    expect(normalizeApiDate('2026-09-15T20:19:42Z')).toBe('2026-09-15T20:19:42Z');
    expect(normalizeApiDate('2026-09-15T23:19:42+03:00')).toBe('2026-09-15T23:19:42+03:00');
    expect(normalizeApiDate('2026-09-15')).toBe('2026-09-15');
  });

  it('parses the three spellings of one instant to the same moment', () => {
    const utc = parseDateSafe('2026-09-15T20:19:00Z')!.getTime();
    expect(parseDateSafe('2026-09-15T20:19:00')!.getTime()).toBe(utc);
    expect(parseDateSafe('2026-09-15T23:19:00+03:00')!.getTime()).toBe(utc);
  });
});

describe('formatDateTime — "15 Sep 2026, 23:19" in the reader\'s zone', () => {
  it('shows the same wall-clock time for offset-less, Z and +03:00 inputs', () => {
    expect(formatDateTime('2026-09-15T20:19:42.1')).toBe('15 Sep 2026, 23:19');
    expect(formatDateTime('2026-09-15T20:19:42Z')).toBe('15 Sep 2026, 23:19');
    expect(formatDateTime('2026-09-15T23:19:42+03:00')).toBe('15 Sep 2026, 23:19');
  });

  it('never appends a zone name and never shows a sentinel', () => {
    expect(formatDateTime('2026-09-15T20:19:42Z')).not.toMatch(/Asia|America|UTC|GMT/);
    expect(formatDateTime('0001-01-01T00:00:00')).toBe('—');
    expect(formatDateTime(null)).toBe('—');
    expect(formatDateTime('not a date', 'unknown')).toBe('unknown');
  });
});

describe('formatDateSafe — a day stays the day it was', () => {
  it('keeps a quote created 15 Sep 23:20 local on the 15th', () => {
    // 20:20Z is 23:20 in Riyadh: the old code read it as 20:20 local, then a NY reader saw the 16th.
    expect(formatDateSafe('2026-09-15T20:20:00')).toBe('15 Sep 2026');
  });

  it('renders a bare calendar day as that day for a reader west of UTC', () => {
    vi.stubEnv('TZ', 'America/New_York');
    try {
      expect(formatDateSafe('2026-09-15')).toBe('15 Sep 2026');
      expect(formatDateTime('2026-09-15')).toBe('15 Sep 2026');
    } finally {
      vi.stubEnv('TZ', 'Asia/Riyadh');
    }
  });
});

describe('formatRelativeReceived — a document cannot arrive in the future', () => {
  const now = new Date('2026-09-15T20:20:00Z');

  it('says "just now" for an offset-less timestamp seconds old, not "in 3 hours"', () => {
    expect(formatRelativeReceived('2026-09-15T20:19:50', now)).toBe('just now');
  });

  it('clamps clock skew to "just now" instead of a future phrase', () => {
    expect(formatRelativeReceived('2026-09-15T20:20:30Z', now)).toBe('just now');
    expect(formatRelativeReceived('2026-09-16T00:00:00Z', now)).not.toMatch(/^in /);
  });

  it('counts back in minutes, hours and days, then falls to the date', () => {
    expect(formatRelativeReceived('2026-09-15T20:15:00Z', now)).toBe('5 minutes ago');
    expect(formatRelativeReceived('2026-09-15T17:20:00Z', now)).toBe('3 hours ago');
    expect(formatRelativeReceived('2026-09-13T20:20:00Z', now)).toBe('2 days ago');
    expect(formatRelativeReceived('2026-08-30T20:20:00Z', now)).toBe('30 Aug 2026');
    expect(formatRelativeReceived(null, now)).toBe('—');
  });
});
