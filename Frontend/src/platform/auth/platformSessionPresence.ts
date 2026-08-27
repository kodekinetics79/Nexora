/**
 * Lightweight platform-session presence signal shared with global UI chrome.
 *
 * Keep this separate from `usePlatformAuth`: RouteAnnouncer is in the initial
 * tenant bundle, while platform login and HTTP code belong to the lazy-loaded
 * control plane. Importing the full hook here would make every tenant download
 * platform authentication code before sign-in.
 */
export const PLATFORM_SESSION_TOKEN_KEY = 'nexora_platform_token';

const PRESENCE_EVENT = 'nexora-platform-session-presence';

export const getPlatformSessionPresence = (): boolean =>
  typeof window !== 'undefined' && window.sessionStorage.getItem(PLATFORM_SESSION_TOKEN_KEY) !== null;

export const subscribePlatformSessionPresence = (callback: () => void): (() => void) => {
  window.addEventListener(PRESENCE_EVENT, callback);
  window.addEventListener('storage', callback);
  return () => {
    window.removeEventListener(PRESENCE_EVENT, callback);
    window.removeEventListener('storage', callback);
  };
};

export const notifyPlatformSessionPresenceChanged = (): void => {
  window.dispatchEvent(new Event(PRESENCE_EVENT));
};
