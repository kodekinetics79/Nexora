import { useEffect, useState } from 'react';
import { Button, Snackbar, Alert } from '@mui/material';
import { BUILD_SHA } from '../../buildInfo';

/**
 * Tells somebody their tab is running an old build, instead of letting them find out by being
 * confused.
 *
 * THE PROBLEM THIS SOLVES. A single-page application keeps running whatever JavaScript it loaded
 * when the tab was opened. Deploy a fix and every open tab carries on with the old one — the
 * screen looks unchanged, the person reasonably reports that nothing changed, and the only way to
 * distinguish that from a change never made is for somebody to drive a browser and compare
 * pixels. That happened here, repeatedly, and it wasted a reviewer's time on a question the
 * application should answer itself.
 *
 * It is worse than confusing in production: an old bundle keeps calling an API that has moved on.
 * The symptoms are arbitrary — a field that silently stops saving, a request refused for reasons
 * the UI has no copy for — and none of them point at the cause.
 *
 * HOW IT WORKS, AND WHY THIS WAY. It re-fetches the SPA shell with `cache: 'no-store'` and looks
 * for the hashed entry bundle named inside it. Rollup renames that file whenever its contents
 * change, so a different name means a different build — no version endpoint to keep in step, no
 * build metadata to publish, and nothing to remember to bump. The shell is served
 * `no-cache, no-store` (see vercel.json), so this reads the deployed truth rather than a cached
 * copy of it.
 *
 * It never reloads on its own. Somebody may be halfway through typing, and the customer screen
 * holds staged edits that a surprise reload would discard — which is the very defect this
 * codebase already fixed once with a navigation guard. It offers; the person decides.
 */

/** How often to look. Rare on purpose: this is a courtesy, not a heartbeat. */
const CHECK_EVERY_MS = 5 * 60 * 1000;

/** Pulls the hashed entry-module name out of the served shell. */
function entryFingerprint(html: string): string | null {
  // Vite emits `<script type="module" crossorigin src="/assets/index-<hash>.js">` in a build, and
  // `/src/main.tsx` in dev — both change identity when the app changes, which is all this needs.
  const match = html.match(/<script[^>]+src="([^"]+)"[^>]*>/i);
  return match ? match[1] : null;
}

export default function NewBuildNotice() {
  const [stale, setStale] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let mine: string | null = null;

    const look = async () => {
      try {
        const response = await fetch(`${window.location.origin}/`, {
          cache: 'no-store',
          headers: { Accept: 'text/html' },
        });
        if (!response.ok) return;
        const fingerprint = entryFingerprint(await response.text());
        if (!fingerprint || cancelled) return;
        if (mine === null) { mine = fingerprint; return; }
        if (fingerprint !== mine) setStale(true);
      } catch {
        // Offline, or the shell is momentarily unavailable mid-deploy. Staying quiet is right:
        // a banner that cries wolf on every flaky network is a banner people learn to dismiss.
      }
    };

    void look();
    const timer = window.setInterval(look, CHECK_EVERY_MS);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, []);

  return (
    <Snackbar
      open={stale}
      anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      // No autoHideDuration: this is not a notification that becomes irrelevant, and dismissing it
      // by inaction is how somebody carries on debugging an old build for an hour.
    >
      <Alert
        severity="info"
        variant="filled"
        action={
          <Button color="inherit" size="small" onClick={() => window.location.reload()}>
            Reload
          </Button>
        }
      >
        A newer version of Nexora has been deployed. You are on build {BUILD_SHA}.
      </Alert>
    </Snackbar>
  );
}
