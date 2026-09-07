import { useCallback, useMemo, useState } from 'react';

/**
 * Edits that accumulate and leave together, instead of sixty-eight buttons that each save one
 * slice of a customer on their own.
 *
 * WHY. The tenant module had sixty-eight independent mutations across twelve tabs, exactly one
 * dirty-state commit bar between them, and no navigate-away guard anywhere. Two consequences,
 * both observed: a half-finished form vanished when a tab was switched, because switching tabs
 * unmounted the component; and an operator who edited two sections and pressed one button
 * silently saved half of what they had done, with nothing on screen saying so.
 *
 * A draft plus one commit makes the second failure unrepresentable — there is no partial save to
 * make — and gives the first something to warn about.
 *
 * WHAT THIS IS NOT. It is not a transaction. The commit still calls the same per-slice endpoints,
 * because those endpoints carry genuinely different authorities and different audit verbs, and
 * collapsing them into one write would hand whoever can edit the cheapest slice the ability to
 * edit all of them. One operator ACTION, several audited writes; if one fails the caller is told
 * which, rather than being left guessing.
 */
export interface StagedChange<T> {
  field: keyof T & string;
  label: string;
  before: string;
  after: string;
}

export interface StagedChanges<T extends object> {
  /** The values as edited, for binding inputs to. */
  draft: T;
  /** The values as loaded, for showing what a change is a change FROM. */
  original: T;
  set: <K extends keyof T>(key: K, value: T[K]) => void;
  /** Only the fields that actually differ, rendered for a human to check before committing. */
  changes: StagedChange<T>[];
  dirty: boolean;
  /** Throw away the edits and go back to what was loaded. */
  discard: () => void;
  /** Adopt a freshly loaded server state as the new baseline, e.g. after a successful commit. */
  rebase: (next: T) => void;
}

const shown = (value: unknown): string =>
  value === null || value === undefined || value === '' ? '—' : String(value);

/**
 * @param initial the values as loaded from the server.
 * @param labels what each field is called on screen, so the review list reads as sentences a
 *   person recognises rather than as property names.
 */
export function useStagedChanges<T extends object>(
  initial: T,
  labels: Partial<Record<keyof T & string, string>>,
): StagedChanges<T> {
  const [original, setOriginal] = useState<T>(initial);
  const [draft, setDraft] = useState<T>(initial);

  const set = useCallback(<K extends keyof T>(key: K, value: T[K]) => {
    setDraft((d) => ({ ...d, [key]: value }));
  }, []);

  const changes = useMemo(() => {
    const out: StagedChange<T>[] = [];
    for (const key of Object.keys(draft) as (keyof T & string)[]) {
      const before = draft[key];
      const after = original[key];
      // Treat null, undefined and empty string as the same absence, so clearing an already-empty
      // field is not reported as a change somebody has to read and dismiss.
      const same = shown(before) === shown(after);
      if (!same) {
        out.push({
          field: key,
          label: labels[key] ?? key,
          before: shown(original[key]),
          after: shown(draft[key]),
        });
      }
    }
    return out;
  }, [draft, original, labels]);

  const discard = useCallback(() => setDraft(original), [original]);

  const rebase = useCallback((next: T) => {
    setOriginal(next);
    setDraft(next);
  }, []);

  return { draft, original, set, changes, dirty: changes.length > 0, discard, rebase };
}
