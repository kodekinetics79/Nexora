import { useCallback, useEffect, useMemo, useState } from 'react';

/**
 * Keeps a half-finished form from evaporating.
 *
 * Across 157 page files this protection existed in exactly ONE:
 * `ExtractionReviewDetailPage`, whose own comment states the reason — "A reviewer who loses twenty
 * minutes of corrections once goes back to Excel permanently." The screens where the money is did
 * not have it. `CreateQuotePage` navigated away on click with no state check, `EditQuotePage`
 * offered a button labelled "Discard" beside Save with no confirmation, and both held every line
 * in `useState`, so a mistaken sidebar click on a 40-line quote lost 25 minutes of pricing with no
 * dialog, no toast and no way back.
 *
 * Extracted as a hook rather than copied a third time. The audit that produced this fix found the
 * same shape everywhere: a good solution written once and never propagated. A hook propagates.
 *
 * Three layers, matching the original:
 *   1. `isDirty` — compared against a baseline captured when the form was last known clean.
 *   2. A debounced `sessionStorage` draft, so a navigation the router never sees still leaves the
 *      work recoverable. Debounced because serialising a large line grid on every keystroke makes
 *      typing stutter for no benefit.
 *   3. A `beforeunload` listener for refresh, tab close and anything else outside the router.
 */

export interface UnsavedWorkGuard<T> {
  /** True when the current value differs from the last baseline. */
  isDirty: boolean;
  /** A draft found in storage when the form mounted, if any. Null once restored or discarded. */
  recoveredDraft: { savedAt: string; value: T } | null;
  /** Accept the recovered draft: clears it from storage and stops offering it. */
  acceptRecovered: () => void;
  /** Throw the recovered draft away. */
  discardRecovered: () => void;
  /** Call after a successful save, or when loading a fresh record, to re-baseline and clear. */
  markSaved: (value: T) => void;
}

interface Options<T> {
  /** Stable per record, e.g. `nexora.quote.edit.41`. Empty string disables the guard entirely. */
  storageKey: string;
  /** The current form value. Compared by JSON, which is what these forms are. */
  value: T;
  /**
   * False while the record is still loading. A baseline captured from an empty form would make
   * the page instantly "dirty" and prompt on every exit — the failure mode that gets a guard
   * switched off again a week later.
   */
  enabled: boolean;
  /** Milliseconds to wait after the last change before writing the draft. */
  debounceMs?: number;
}

const serialise = (value: unknown): string => {
  try {
    return JSON.stringify(value) ?? '';
  } catch {
    return '';
  }
};

export function useUnsavedWorkGuard<T>({
  storageKey,
  value,
  enabled,
  debounceMs = 800,
}: Options<T>): UnsavedWorkGuard<T> {
  // State, not a ref. A ref does not re-render, so `markSaved` would leave `isDirty` stale until
  // something else happened to render — the form would keep claiming unsaved work immediately
  // after a successful save, and prompt on the way out.
  const [baseline, setBaseline] = useState<string | null>(null);
  const [recoveredDraft, setRecoveredDraft] = useState<{ savedAt: string; value: T } | null>(null);
  const [recoveryChecked, setRecoveryChecked] = useState(false);

  const current = useMemo(() => serialise(value), [value]);

  const clearStored = useCallback(() => {
    if (!storageKey) return;
    try {
      sessionStorage.removeItem(storageKey);
    } catch {
      // Storage blocked; the in-page guard still protects this session.
    }
  }, [storageKey]);

  // Look for an abandoned draft ONCE, before the first baseline is taken, so a draft written by a
  // previous visit is not mistaken for the current form's own writes.
  useEffect(() => {
    if (!enabled || recoveryChecked || !storageKey) return;
    setRecoveryChecked(true);
    try {
      const raw = sessionStorage.getItem(storageKey);
      if (!raw) return;
      const parsed = JSON.parse(raw) as { savedAt?: string; value?: T };
      if (parsed && parsed.value !== undefined) {
        setRecoveredDraft({ savedAt: parsed.savedAt ?? '', value: parsed.value });
      }
    } catch {
      // A malformed draft is not worth surfacing; drop it rather than blocking the page.
      clearStored();
    }
  }, [enabled, recoveryChecked, storageKey, clearStored]);

  // Baseline the form the first time it is populated.
  useEffect(() => {
    if (!enabled || baseline !== null) return;
    setBaseline(current);
  }, [enabled, baseline, current]);

  const isDirty = enabled && baseline !== null && baseline !== current;

  // Persist unsent work so a mistaken sidebar click costs nothing.
  useEffect(() => {
    if (!enabled || !storageKey) return;
    if (!isDirty) {
      clearStored();
      return;
    }
    const handle = window.setTimeout(() => {
      try {
        sessionStorage.setItem(
          storageKey,
          JSON.stringify({ savedAt: new Date().toISOString(), value }),
        );
      } catch {
        // Storage full or blocked — beforeunload still protects the session.
      }
    }, debounceMs);
    return () => window.clearTimeout(handle);
  }, [enabled, storageKey, isDirty, value, debounceMs, clearStored]);

  // Refresh, tab close, and any navigation the router never sees.
  useEffect(() => {
    if (!isDirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [isDirty]);

  const markSaved = useCallback((saved: T) => {
    setBaseline(serialise(saved));
    clearStored();
    setRecoveredDraft(null);
  }, [clearStored]);

  const acceptRecovered = useCallback(() => {
    clearStored();
    setRecoveredDraft(null);
  }, [clearStored]);

  const discardRecovered = useCallback(() => {
    clearStored();
    setRecoveredDraft(null);
  }, [clearStored]);

  return { isDirty, recoveredDraft, acceptRecovered, discardRecovered, markSaved };
}

export default useUnsavedWorkGuard;
