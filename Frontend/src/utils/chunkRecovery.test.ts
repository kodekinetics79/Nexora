import { describe, expect, it } from 'vitest';
import { claimChunkRecovery, isStaleDeploymentChunkError } from './chunkRecovery';

describe('stale deployment chunk recovery', () => {
  it('recognises Vite and webpack lazy-chunk failures', () => {
    expect(isStaleDeploymentChunkError(new TypeError('Failed to fetch dynamically imported module: /assets/Lead.js'))).toBe(true);
    expect(isStaleDeploymentChunkError(new Error('ChunkLoadError: Loading chunk 42 failed'))).toBe(true);
    expect(isStaleDeploymentChunkError(new Error('ordinary render failure'))).toBe(false);
  });

  it('recognises the Safari, Firefox and stylesheet-preload wordings of the same failure', () => {
    // Only Chrome's wording used to match, so Safari and Firefox users got the full-screen crash
    // card with no recovery after every deploy.
    expect(isStaleDeploymentChunkError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isStaleDeploymentChunkError(new TypeError('error loading dynamically imported module: https://nexora1-ai.vercel.app/assets/Quotes-1a2b.js'))).toBe(true);
    expect(isStaleDeploymentChunkError(new Error('Unable to preload CSS for /assets/DecidePage-9f8e.css'))).toBe(true);
    expect(isStaleDeploymentChunkError(new TypeError("Failed to load module script: Expected a JavaScript-or-Wasm module script but the server responded with a MIME type of \"text/html\"."))).toBe(true);
    expect(isStaleDeploymentChunkError(new TypeError('Cannot read properties of undefined'))).toBe(false);
  });

  it('allows one automatic reload per route during the cooldown', () => {
    const values = new Map<string, string>();
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
    };

    expect(claimChunkRecovery(storage, '/procurement/leads/view/493', 1_000)).toBe(true);
    expect(claimChunkRecovery(storage, '/procurement/leads/view/493', 2_000)).toBe(false);
    expect(claimChunkRecovery(storage, '/procurement/leads/view/494', 2_000)).toBe(true);
    expect(claimChunkRecovery(storage, '/procurement/leads/view/493', 302_000)).toBe(true);
  });
});
