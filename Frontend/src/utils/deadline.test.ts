import { afterEach, describe, expect, it, vi } from 'vitest';
import { deadlineWords } from './deadline';
import { daysUntil } from '../pages/Leads/Decide/decideRules';

const now = new Date(2026, 8, 27, 15, 0);

describe('deadlineWords', () => {
  it('says the deadline the way a rep reads it', () => {
    expect(deadlineWords(null, now)).toEqual({ text: 'No deadline', tone: 'none' });
    expect(deadlineWords('2026-09-27T23:00:00', now)).toEqual({ text: 'Due today', tone: 'soon' });
    expect(deadlineWords('2026-09-28T08:00:00', now)).toEqual({ text: 'Due tomorrow', tone: 'soon' });
    expect(deadlineWords('2026-09-30T08:00:00', now)).toEqual({ text: '3 days left', tone: 'near' });
    expect(deadlineWords('2026-10-15T08:00:00', now).tone).toBe('calm');
    expect(deadlineWords('2026-09-26T08:00:00', now)).toEqual({ text: '1 day late', tone: 'late' });
    expect(deadlineWords('2026-09-20T08:00:00', now)).toEqual({ text: '7 days late', tone: 'late' });
  });
});

describe('one count on every screen (pilot audit HT-07)', () => {
  afterEach(() => { vi.unstubAllEnvs(); });

  it('says "Due tomorrow" for a bid closing on the 29th when it is the 28th in New York', () => {
    vi.stubEnv('TZ', 'America/New_York');
    const evening = new Date(2026, 8, 28, 21, 0);
    // "Due today · 29 Sep 2026" on the 28th: the stored day was moved into New York's zone first.
    expect(deadlineWords('2026-09-29T00:00:00', evening).text).toBe('Due tomorrow');
    expect(deadlineWords('2026-09-13T01:45:00', new Date(2026, 8, 12, 20, 0)).text).toBe('Due tomorrow');
  });

  it('gives the Leads list and Decide the same number for the same bid', () => {
    for (const zone of ['America/New_York', 'Asia/Riyadh', 'Pacific/Auckland']) {
      vi.stubEnv('TZ', zone);
      const now = new Date(2026, 8, 28, 15, 0);
      expect(deadlineWords('2026-10-08T15:00:00', now).text).toBe('10 days left');
      expect(daysUntil('2026-10-08T15:00:00', now)).toBe(10);
      expect(deadlineWords('2026-06-09T00:00:00', now).text).toBe('111 days late');
    }
  });
});
