const CHUNK_RECOVERY_WINDOW_MS = 5 * 60 * 1000;
const CHUNK_RECOVERY_PREFIX = 'nexora:chunk-recovery:';

const errorMessage = (error: unknown): string => {
  if (error instanceof Error) return `${error.name}: ${error.message}`;
  return String(error ?? '');
};

/**
 * Whether an error means "this tab asked for a code file that belonged to an older deployment".
 *
 * Every browser words the failure differently, and the old pattern only knew Chrome's. Safari says
 * "Importing a module script failed", Firefox "error loading dynamically imported module", and
 * Vite's preload helper throws "Unable to preload CSS" when the page's stylesheet went first. An
 * unrecognised wording used to fall through to the full-screen crash card with no recovery at all.
 */
export const isStaleDeploymentChunkError = (error: unknown): boolean => {
  const message = errorMessage(error);
  return /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|Unable to preload CSS|Expected a JavaScript(?:-or-Wasm)? module script|Failed to load module script|RouteChunkLoadError|ChunkLoadError|Loading (?:CSS )?chunk [\w-]+ failed/i.test(message);
};

export const claimChunkRecovery = (
  storage: Pick<Storage, 'getItem' | 'setItem'>,
  locationKey: string,
  now = Date.now(),
): boolean => {
  const key = `${CHUNK_RECOVERY_PREFIX}${locationKey}`;
  const previous = Number(storage.getItem(key));
  if (Number.isFinite(previous) && previous > 0 && now - previous < CHUNK_RECOVERY_WINDOW_MS) {
    return false;
  }

  storage.setItem(key, String(now));
  return true;
};
