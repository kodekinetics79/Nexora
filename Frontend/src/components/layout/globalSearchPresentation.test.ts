import { describe, expect, it } from 'vitest';
import { GLOBAL_SEARCH_FAILURE_MESSAGE, GLOBAL_SEARCH_LABEL } from './globalSearchPresentation';

describe('global search presentation', () => {
  it('describes the records-only corpus truthfully', () => {
    expect(GLOBAL_SEARCH_LABEL).toBe('Search records');
  });

  it('uses a stable failure message that exposes no server detail', () => {
    expect(GLOBAL_SEARCH_FAILURE_MESSAGE).toBe('Search is unavailable right now. Try again in a moment.');
    expect(GLOBAL_SEARCH_FAILURE_MESSAGE).not.toMatch(/gateway|authorization|exception|stack/i);
  });
});
