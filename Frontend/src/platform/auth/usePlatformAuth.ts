// ---------------------------------------------------------------------------
// Platform (owner) authentication — PLACEHOLDER
//
// The platform console lives ABOVE tenants and must be gated on a
// `scope=platform` claim / dedicated platform token (ADR-0005), NOT on the
// tenant-scoped RBAC used elsewhere in the app.
//
// For now this reads a local flag so the console can be built and demoed.
//
// TODO(platform-auth): replace the flag check with the real gate, e.g.
//   - decode the JWT (jwt-decode is already a dependency) and assert
//     `payload.scope === 'platform'` / `payload.roles.includes('platform_owner')`
//   - OR check a dedicated `platformToken` issued by the platform IdP
//   - wire `enterPlatform` / `exitPlatform` to acquire / drop that token.
// ---------------------------------------------------------------------------

import { useCallback, useSyncExternalStore } from 'react';

const STORAGE_KEY = 'nexora.platformScope';

// Simple external store so every component re-renders when the flag flips.
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

const subscribe = (cb: () => void) => {
  listeners.add(cb);
  window.addEventListener('storage', cb);
  return () => {
    listeners.delete(cb);
    window.removeEventListener('storage', cb);
  };
};

const getSnapshot = () => localStorage.getItem(STORAGE_KEY) === 'true';

export interface PlatformAuth {
  /** Whether the current principal holds the `scope=platform` claim. */
  isPlatformOwner: boolean;
  /** Grant platform scope (demo shim — real impl acquires a platform token). */
  enterPlatform: () => void;
  /** Drop platform scope. */
  exitPlatform: () => void;
}

export const usePlatformAuth = (): PlatformAuth => {
  const isPlatformOwner = useSyncExternalStore(subscribe, getSnapshot, () => false);

  const enterPlatform = useCallback(() => {
    // TODO(platform-auth): exchange for a real platform-scoped token here.
    localStorage.setItem(STORAGE_KEY, 'true');
    emit();
  }, []);

  const exitPlatform = useCallback(() => {
    localStorage.removeItem(STORAGE_KEY);
    emit();
  }, []);

  return { isPlatformOwner, enterPlatform, exitPlatform };
};
