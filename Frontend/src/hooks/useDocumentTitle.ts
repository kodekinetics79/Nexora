import { useEffect } from 'react';

/**
 * Document-title plumbing for WCAG 2.1 SC 2.4.2 (Page Titled).
 *
 * The app is a single-page app, so without this every one of the ~110 routes
 * reports the static `<title>` from index.html. Screen-reader users announce
 * the title on every navigation, and browser history/tabs/bookmarks are all
 * indistinguishable without it.
 */

export const APP_NAME = 'NEXORA';

/** Fallback that matches index.html, used when a route has no mapped title. */
export const DEFAULT_DOCUMENT_TITLE = 'NEXORA | The Intelligence Platform';

export const formatDocumentTitle = (title?: string | null): string => {
  const trimmed = title?.trim();
  return trimmed ? `${trimmed} | ${APP_NAME}` : DEFAULT_DOCUMENT_TITLE;
};

/**
 * Sets `document.title` to `"<title> | NEXORA"` for as long as the calling
 * component is mounted. Pass `null`/`undefined` to fall back to the app title.
 *
 * Normally you do not need to call this per page: `RouteAnnouncer` (mounted
 * once in App.tsx) resolves a title for every route from `routeTitles.ts`.
 * Call it directly only for pages whose title is not derivable from the path.
 */
export function useDocumentTitle(title?: string | null): void {
  useEffect(() => {
    document.title = formatDocumentTitle(title);
  }, [title]);
}

export default useDocumentTitle;
