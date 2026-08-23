import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { useUnsavedWorkGuard } from './useUnsavedWorkGuard';

/**
 * The protection that existed in exactly one of 157 page files.
 *
 * `CreateQuotePage` navigated away on click with no state check; `EditQuotePage` offered a button
 * labelled "Discard" next to Save with no confirmation. Both held every line in `useState`, so a
 * mistaken sidebar click 25 minutes into a 40-line quote lost all of it silently.
 */

const KEY = 'nexora.quote.edit.41';

beforeEach(() => {
  sessionStorage.clear();
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

const setup = (initial: unknown, enabled = true) =>
  renderHook(
    ({ value, enabled: on }) => useUnsavedWorkGuard({ storageKey: KEY, value, enabled: on }),
    { initialProps: { value: initial, enabled } },
  );

describe('dirty tracking', () => {
  it('is clean when the form still matches the record it loaded', () => {
    const { result } = setup({ lines: [{ price: 100 }] });
    expect(result.current.isDirty).toBe(false);
  });

  it('becomes dirty when a price changes', () => {
    const { result, rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    expect(result.current.isDirty).toBe(true);
  });

  it('goes clean again if the user undoes the change by hand', () => {
    const { result, rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    rerender({ value: { lines: [{ price: 100 }] }, enabled: true });
    expect(result.current.isDirty).toBe(false);
  });

  it('does not baseline an empty form while the record is still loading', () => {
    // Baselining before load would make the page instantly dirty and prompt on every exit —
    // the failure mode that gets a guard switched back off a week later.
    const { result, rerender } = setup({}, false);
    expect(result.current.isDirty).toBe(false);

    rerender({ value: { lines: [{ price: 100 }] }, enabled: true });
    expect(result.current.isDirty).toBe(false);

    rerender({ value: { lines: [{ price: 999 }] }, enabled: true });
    expect(result.current.isDirty).toBe(true);
  });
});

describe('draft persistence', () => {
  it('writes a draft after the debounce, not on every keystroke', () => {
    const { rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });

    expect(sessionStorage.getItem(KEY)).toBeNull();
    act(() => { vi.advanceTimersByTime(900); });
    expect(sessionStorage.getItem(KEY)).not.toBeNull();
  });

  it('stores the actual work, so it can be recovered', () => {
    const { rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    act(() => { vi.advanceTimersByTime(900); });

    const stored = JSON.parse(sessionStorage.getItem(KEY) as string);
    expect(stored.value).toEqual({ lines: [{ price: 250 }] });
    expect(stored.savedAt).toBeTruthy();
  });

  it('clears the draft once the form matches the record again', () => {
    const { rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    act(() => { vi.advanceTimersByTime(900); });
    expect(sessionStorage.getItem(KEY)).not.toBeNull();

    rerender({ value: { lines: [{ price: 100 }] }, enabled: true });
    act(() => { vi.advanceTimersByTime(900); });
    expect(sessionStorage.getItem(KEY)).toBeNull();
  });

  it('clears the draft after a successful save', () => {
    const { result, rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    act(() => { vi.advanceTimersByTime(900); });

    act(() => { result.current.markSaved({ lines: [{ price: 250 }] }); });
    expect(sessionStorage.getItem(KEY)).toBeNull();
    expect(result.current.isDirty).toBe(false);
  });
});

describe('recovering work from a previous visit', () => {
  it('offers a draft left behind by an earlier session', () => {
    sessionStorage.setItem(KEY, JSON.stringify({
      savedAt: '2026-08-20T10:00:00.000Z',
      value: { lines: [{ price: 4242 }] },
    }));

    const { result } = setup({ lines: [{ price: 100 }] });
    expect(result.current.recoveredDraft?.value).toEqual({ lines: [{ price: 4242 }] });
    expect(result.current.recoveredDraft?.savedAt).toBe('2026-08-20T10:00:00.000Z');
  });

  it('offers nothing when there is nothing to recover', () => {
    const { result } = setup({ lines: [{ price: 100 }] });
    expect(result.current.recoveredDraft).toBeNull();
  });

  it('drops a malformed draft rather than blocking the page', () => {
    sessionStorage.setItem(KEY, '{ not json');
    const { result } = setup({ lines: [{ price: 100 }] });

    expect(result.current.recoveredDraft).toBeNull();
    expect(sessionStorage.getItem(KEY)).toBeNull();
  });

  it('stops offering the draft once it is taken or thrown away', () => {
    sessionStorage.setItem(KEY, JSON.stringify({ savedAt: 'x', value: { lines: [] } }));
    const { result } = setup({ lines: [{ price: 100 }] });

    act(() => { result.current.discardRecovered(); });
    expect(result.current.recoveredDraft).toBeNull();
    expect(sessionStorage.getItem(KEY)).toBeNull();
  });
});

describe('the browser-level guard', () => {
  it('registers beforeunload only while there is work to lose', () => {
    const add = vi.spyOn(window, 'addEventListener');
    const remove = vi.spyOn(window, 'removeEventListener');

    const { rerender } = setup({ lines: [{ price: 100 }] });
    expect(add.mock.calls.some(([e]) => e === 'beforeunload')).toBe(false);

    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });
    expect(add.mock.calls.some(([e]) => e === 'beforeunload')).toBe(true);

    // Undoing the change must release the guard, or every clean exit prompts.
    rerender({ value: { lines: [{ price: 100 }] }, enabled: true });
    expect(remove.mock.calls.some(([e]) => e === 'beforeunload')).toBe(true);

    add.mockRestore();
    remove.mockRestore();
  });

  it('asks the browser to prompt when the tab is closed mid-edit', () => {
    const { rerender } = setup({ lines: [{ price: 100 }] });
    rerender({ value: { lines: [{ price: 250 }] }, enabled: true });

    const event = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
  });
});
