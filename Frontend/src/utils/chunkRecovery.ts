const CHUNK_RECOVERY_WINDOW_MS = 5 * 60 * 1000;
const CHUNK_RECOVERY_PREFIX = 'nexora:chunk-recovery:';

const errorMessage = (error: unknown): string => {
  if (error instanceof Error) return `${error.name}: ${error.message}`;
  return String(error ?? '');
};

export const isStaleDeploymentChunkError = (error: unknown): boolean => {
  const message = errorMessage(error);
  return /Failed to fetch dynamically imported module|ChunkLoadError|Loading chunk [\w-]+ failed/i.test(message);
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
