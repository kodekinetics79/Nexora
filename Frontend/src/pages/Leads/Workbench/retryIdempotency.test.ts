import { describe, expect, it, vi } from 'vitest';
import { retryOperation } from './retryIdempotency';

describe('retryOperation', () => {
  it('reuses the same operation key for an identical retry', () => {
    const createId = vi.fn().mockReturnValueOnce('first').mockReturnValueOnce('second');
    const first = retryOperation(null, 'lead-fit', 42, { criteria: [{ code: 'A', decision: 'PASS' }] }, createId);
    const retry = retryOperation(first, 'lead-fit', 42, { criteria: [{ decision: 'PASS', code: 'A' }] }, createId);

    expect(retry).toBe(first);
    expect(retry.key).toBe('lead-fit:42:first');
    expect(createId).toHaveBeenCalledTimes(1);
  });

  it('creates a new operation key when the business payload changes', () => {
    const createId = vi.fn().mockReturnValueOnce('first').mockReturnValueOnce('second');
    const first = retryOperation(null, 'lead-participation-draft', 42, { commit: false, lines: [{ decision: 'Pending' }] }, createId);
    const changed = retryOperation(first, 'lead-participation-draft', 42, { commit: false, lines: [{ decision: 'Bid' }] }, createId);

    expect(changed.key).toBe('lead-participation-draft:42:second');
    expect(changed.fingerprint).not.toBe(first.fingerprint);
  });
});
