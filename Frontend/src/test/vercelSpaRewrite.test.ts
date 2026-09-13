import { describe, expect, it } from 'vitest';
import frontendVercelJson from '../../vercel.json?raw';

/**
 * The single-page rewrite must not answer a missing code file with the app shell.
 *
 * `/(.*) -> /index.html` served index.html (HTTP 200, text/html) for `/assets/Old-hash.js` after
 * every deploy. The browser then failed the import with a MIME-type error instead of a clean 404,
 * which only Chrome's recovery pattern recognised.
 *
 * The repository-root vercel.json carries the identical rule. Vite will not import a file outside
 * Frontend/, so this suite cannot read that copy; scripts/deploy/verify-deployment-contract.sh
 * compares only the two files' security headers today, not their rewrites.
 */

interface VercelConfig {
  rewrites?: { source: string; destination: string }[];
}

/** Vercel sources are path-to-regexp patterns anchored to the whole path; this one uses only a group. */
const matches = (source: string, path: string) => new RegExp(`^${source}$`).test(path);

describe('Vercel SPA rewrite', () => {
  const config: VercelConfig = JSON.parse(frontendVercelJson);

  it('sends app routes to the shell but never answers a missing asset with it', () => {
    const spa = config.rewrites?.find((rule) => rule.destination === '/index.html');
    expect(spa).toBeDefined();
    expect(matches(spa!.source, '/procurement/leads/7/workbench')).toBe(true);
    expect(matches(spa!.source, '/sales/quotes/edit/9')).toBe(true);
    expect(matches(spa!.source, '/')).toBe(true);
    expect(matches(spa!.source, '/assets/LeadDecidePage-OLDHASH1.js')).toBe(false);
    expect(matches(spa!.source, '/assets/index-OLDHASH2.css')).toBe(false);
  });
});
