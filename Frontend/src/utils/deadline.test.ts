import { describe, expect, it } from 'vitest';
import { deadlineWords } from './deadline';

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
