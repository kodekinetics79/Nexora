import { describe, expect, it } from 'vitest';
import { pollIntervalFor } from './ingestionPolling';

describe('active ingestion polling', () => {
  it('never leaves a completed document stale for more than five seconds', () => {
    expect(pollIntervalFor(0)).toBe(2_000);
    expect(pollIntervalFor(6)).toBe(5_000);
    expect(pollIntervalFor(60)).toBe(5_000);
  });
});
