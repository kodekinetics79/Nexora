/**
 * What build is running, available to the app and to anyone reading a screenshot.
 *
 * WHY THIS EXISTS. "Nothing changed" was an unanswerable question. The reviewer could not tell a
 * cached bundle from a change that was never made, and neither could the person who wrote it
 * without launching a browser and comparing pixels. A build that cannot identify itself makes
 * every report of a stale screen a guess.
 *
 * Injected by Vite at build time (see vite.config.ts). In dev the values still resolve, so the
 * stamp is meaningful in the review environment, which is precisely where the confusion happened.
 */
declare const __BUILD_SHA__: string;
declare const __BUILT_AT__: string;

export const BUILD_SHA: string = typeof __BUILD_SHA__ === 'string' ? __BUILD_SHA__ : 'unknown';
export const BUILT_AT: string = typeof __BUILT_AT__ === 'string' ? __BUILT_AT__ : '';

/** A trailing "+" means the build was made from a working tree with uncommitted changes. */
export const BUILD_IS_DIRTY = BUILD_SHA.endsWith('+');

/** Short, human-readable, safe to print in a footer or read off a screenshot. */
export function buildLabel(): string {
  if (!BUILT_AT) return BUILD_SHA;
  const at = new Date(BUILT_AT);
  const when = Number.isNaN(at.getTime())
    ? BUILT_AT
    : at.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  return `${BUILD_SHA} · ${when}`;
}
