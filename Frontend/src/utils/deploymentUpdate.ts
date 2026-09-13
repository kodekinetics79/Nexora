import { hasUnsavedWork } from '../hooks/unsavedWorkRegistry';
import { claimChunkRecovery, isStaleDeploymentChunkError } from './chunkRecovery';

/**
 * "Nexora was updated while this tab was open" — the one piece of state every recovery path shares.
 *
 * Production redeploys many times a day, and each deploy replaces the hashed code files. A tab
 * opened before the deploy can then fail to load a screen it has not visited yet. The old answer
 * was to reload the page on the spot, or to replace the whole application with a crash card. Both
 * take the screen away from the person using it.
 *
 * The rule now: nothing reloads while someone is looking at their work. A quiet notice offers the
 * reload, and the page is only reloaded as part of a navigation the person makes themselves —
 * never while a form holds unsaved work, and at most once per address every few minutes, so a
 * build that is genuinely broken cannot loop.
 *
 * Module state rather than context: the boundaries that detect the update are class components
 * deep in the tree, the notice sits beside the router, and a flag with a handful of readers does
 * not need a provider.
 */

type Listener = () => void;

let updateAvailable = false;
/** Recovery cards currently on screen. The notice stands down while one is showing its own reload. */
const recoveryCards = new Set<object>();
const listeners = new Set<Listener>();

const emit = () => {
  listeners.forEach((listener) => listener());
};

/** Seam for tests: jsdom cannot perform a real document reload. */
export const pageReloader = {
  reload: (): void => {
    window.location.reload();
  },
};

export const markDeploymentUpdated = (): void => {
  if (updateAvailable) return;
  updateAvailable = true;
  emit();
};

export const isDeploymentUpdated = (): boolean => updateAvailable;

/** True when the quiet notice should be visible: an update exists and no card already offers it. */
export const shouldShowDeploymentNotice = (): boolean => updateAvailable && recoveryCards.size === 0;

export const setRecoveryCardShown = (owner: object, shown: boolean): void => {
  const had = recoveryCards.has(owner);
  if (shown === had) return;
  if (shown) recoveryCards.add(owner);
  else recoveryCards.delete(owner);
  emit();
};

export const subscribeDeploymentUpdate = (listener: Listener): (() => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};

/**
 * Loads the current address fresh, if and only if that is safe right now.
 *
 * Refused while any form reports unsaved work, and refused a second time for the same address
 * inside the recovery window (the loop guard). Returns whether a reload was started.
 *
 * @param locationKey The router's current path. Callers inside the router pass it so the loop guard
 *                    keys on the address React is actually rendering.
 */
export const reloadForDeploymentUpdate = (
  locationKey: string = `${window.location.pathname}${window.location.search}`,
): boolean => {
  if (hasUnsavedWork()) return false;
  try {
    if (!claimChunkRecovery(window.sessionStorage, locationKey)) return false;
  } catch {
    // Storage blocked (hardened/private contexts): without the loop guard, do not reload by itself.
    return false;
  }
  pageReloader.reload();
  return true;
};

/**
 * Vite announces a failed code or stylesheet preload before it rethrows. Listening lets a failure
 * that never reaches a boundary still raise the notice. The event is deliberately NOT cancelled:
 * cancelling makes the import resolve to nothing, which crashes the lazy screen with a worse error.
 */
export const installDeploymentUpdateListener = (target: Window = window): (() => void) => {
  const onPreloadError = (event: Event) => {
    const payload = (event as Event & { payload?: unknown }).payload;
    if (payload === undefined || isStaleDeploymentChunkError(payload)) markDeploymentUpdated();
  };
  target.addEventListener('vite:preloadError', onPreloadError);
  return () => target.removeEventListener('vite:preloadError', onPreloadError);
};

/** Test-only reset. */
export const resetDeploymentUpdateForTests = (): void => {
  updateAvailable = false;
  recoveryCards.clear();
  emit();
};
