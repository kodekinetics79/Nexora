import { describe, expect, it } from 'vitest';
import { SCOPE_UNRESOLVED, priorWindow, scopeWords } from './scopeWords';

describe('scopeWords', () => {
  it('reads the release-01 vocabulary', () => {
    expect(scopeWords('tenant')).toBe('Company-wide');
    expect(scopeWords('managed_scope')).toBe('Your managed scope');
    expect(scopeWords('assigned_accounts')).toBe('Your assigned accounts');
  });

  // The commercial-intelligence controller spells the narrowest tier differently. Both wire words
  // have to land on the same sentence or the same reader would see two different scopes on one
  // screen depending on which endpoint answered.
  it('reads the commercial-intelligence vocabulary onto the same words', () => {
    expect(scopeWords('assigned_to_me')).toBe('Your assigned accounts');
    expect(scopeWords('assigned_to_me')).toBe(scopeWords('assigned_accounts'));
  });

  it('returns null for anything it does not recognise, rather than printing the wire word', () => {
    expect(scopeWords('region_scope')).toBeNull();
    expect(scopeWords('')).toBeNull();
    expect(scopeWords(undefined)).toBeNull();
    expect(scopeWords(null)).toBeNull();
    expect(SCOPE_UNRESOLVED).toBe('Scope not stated');
  });
});

describe('priorWindow', () => {
  it('abuts the given window with no shared day and the same length', () => {
    expect(priorWindow('2026-01-01', '2026-01-30')).toEqual({ from: '2025-12-02', to: '2025-12-31' });
  });

  it('handles a single-day window', () => {
    expect(priorWindow('2026-03-10', '2026-03-10')).toEqual({ from: '2026-03-09', to: '2026-03-09' });
  });

  it('refuses to invent a comparison period', () => {
    expect(priorWindow('2026-01-30', '2026-01-01')).toBeNull();
    expect(priorWindow('not-a-date', '2026-01-01')).toBeNull();
    expect(priorWindow(null, '2026-01-01')).toBeNull();
    expect(priorWindow('2026-01-01', undefined)).toBeNull();
  });
});
