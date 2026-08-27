import React from 'react';
import { useLocation } from 'react-router-dom';
import { Box } from '@mui/material';
import useDocumentTitle from '../../hooks/useDocumentTitle';
import { resolveRouteTitle } from '../../utils/routeTitles';
import {
  getPlatformSessionPresence,
  subscribePlatformSessionPresence,
} from '../../platform/auth/platformSessionPresence';
import { MAIN_CONTENT_ID } from './SkipLink';

/** Roughly 500ms of frames — enough for a lazy route chunk to mount `<main>`. */
const MAX_FOCUS_ATTEMPTS = 30;

/**
 * Cross-cutting client-side-navigation accessibility, mounted once in App.tsx.
 *
 * A single-page app changes the whole page without a document load, so three
 * things browsers normally do for free have to be done by hand:
 *
 * 1. **Page title** (WCAG 2.1 SC 2.4.2) — resolved per route from
 *    `utils/routeTitles.ts`.
 * 2. **Announcement** (SC 4.1.3 Status Messages) — a polite live region tells
 *    screen-reader users which page they landed on.
 * 3. **Focus + scroll reset** (SC 2.4.3 Focus Order) — focus is moved to the
 *    `<main>` landmark and the viewport is scrolled to the top, so keyboard and
 *    screen-reader users start at the new content instead of being stranded
 *    wherever the previous page's focus happened to be.
 */
const RouteAnnouncer: React.FC = () => {
  const { pathname, search, hash } = useLocation();
  const isPlatformAuthed = React.useSyncExternalStore(
    subscribePlatformSessionPresence,
    getPlatformSessionPresence,
    () => false,
  );
  // PlatformGuard renders the operator sign-in in place at the requested
  // console URL. Until a platform session exists, announcing the destination
  // page (for example "Tenants") claims that protected content has loaded
  // when the person is actually still at the authentication boundary.
  const routeTitle = pathname.startsWith('/platform') && !isPlatformAuthed
    ? 'Platform Console Sign In'
    : resolveRouteTitle(pathname);
  const [announcement, setAnnouncement] = React.useState('');
  // Tracks the last location we reacted to. A boolean "first render" flag is
  // not enough: React StrictMode double-invokes effects in development, so the
  // second invocation would sail past it and steal focus on initial page load.
  const lastHandledLocation = React.useRef<string | null>(null);

  useDocumentTitle(routeTitle);

  React.useEffect(() => {
    // The effective title is part of the location identity. PlatformGuard
    // renders sign-in in place; when a platform session is established the
    // path stays the same but the screen and title legitimately change.
    const locationKey = `${pathname}${search}${hash}|${routeTitle ?? ''}`;
    // Same location as last time — a StrictMode re-run or an unrelated
    // re-render, not a navigation.
    if (lastHandledLocation.current === locationKey) return;

    const isInitialRender = lastHandledLocation.current === null;
    lastHandledLocation.current = locationKey;

    // Never hijack the very first render: the browser has already put focus and
    // scroll where the user expects on a real document load.
    if (isInitialRender) return;

    if (!hash) {
      window.scrollTo({ top: 0, left: 0 });
    }

    setAnnouncement(routeTitle ? `${routeTitle}, page loaded.` : 'Page loaded.');

    // The route element may still be behind a <Suspense> fallback while its
    // chunk loads, so retry across frames until <main> exists.
    const focusOrigin = document.activeElement;
    let attempts = 0;
    let frame = 0;

    const attemptFocus = () => {
      // Bail out if the user has already moved focus somewhere themselves.
      const active = document.activeElement;
      const userMovedFocus =
        active !== focusOrigin && active !== document.body && active !== null;
      if (userMovedFocus) return;

      const main = document.getElementById(MAIN_CONTENT_ID);
      if (main) {
        main.focus({ preventScroll: true });
        return;
      }
      if (attempts++ < MAX_FOCUS_ATTEMPTS) {
        frame = window.requestAnimationFrame(attemptFocus);
      }
    };

    attemptFocus();
    return () => window.cancelAnimationFrame(frame);
  }, [pathname, search, hash, routeTitle]);

  return (
    <Box
      role="status"
      aria-live="polite"
      aria-atomic="true"
      sx={{
        position: 'absolute',
        width: 1,
        height: 1,
        padding: 0,
        margin: -1,
        overflow: 'hidden',
        clip: 'rect(0 0 0 0)',
        clipPath: 'inset(50%)',
        whiteSpace: 'nowrap',
        border: 0,
      }}
    >
      {announcement}
    </Box>
  );
};

export default RouteAnnouncer;
