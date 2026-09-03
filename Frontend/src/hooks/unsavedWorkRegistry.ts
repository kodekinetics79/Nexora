/**
 * The one place the app asks "is anything on screen unsaved?" before it navigates.
 *
 * `useUnsavedWorkGuard` covers refresh and tab-close through `beforeunload`, and each quote page's
 * own Cancel button asks before leaving. Nothing covered the rail: a click on "Inbox" in the
 * sidebar while a 40-line quote was half priced left the page with no question asked, and the
 * only trace of the work was a sessionStorage draft the quote pages never read back. The app
 * mounts `BrowserRouter` (not a data router), so `useBlocker` is unavailable — this is the small
 * dirty flag the rail's navigation handler consults instead.
 *
 * Module state, not context: the Sidebar and the form are far apart in the tree, and a flag
 * with two readers does not need a provider.
 */

const unsaved = new Map<string, string>();

/** Register (message) or clear (null) a form's unsaved state under a stable key. */
export const setUnsavedWork = (key: string, message: string | null): void => {
  if (message) unsaved.set(key, message);
  else unsaved.delete(key);
};

export const hasUnsavedWork = (): boolean => unsaved.size > 0;

/**
 * True when it is fine to navigate: nothing is dirty, or the person confirmed leaving. Uses the
 * same `window.confirm` the Cancel buttons use, so the question reads identically wherever the
 * exit is.
 */
export const confirmLeavingUnsavedWork = (): boolean => {
  if (unsaved.size === 0) return true;
  const message = unsaved.values().next().value
    ?? 'Leave without saving? The work you have entered on this page will be lost.';
  return window.confirm(message);
};

/** Test-only reset. */
export const clearAllUnsavedWork = (): void => unsaved.clear();
