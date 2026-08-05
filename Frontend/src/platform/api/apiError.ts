import { isAxiosError } from 'axios';

/**
 * Extracts the backend's `{ error: "…" }` problem message from a failed
 * platform call so guard-rail conflicts (last-Owner, self-deactivation,
 * lifecycle transitions, immutable rate cards, …) surface verbatim in
 * snackbars instead of a generic failure line.
 */
export const platformErrorMessage = (error: unknown, fallback: string): string => {
  if (isAxiosError(error)) {
    const data = error.response?.data as { error?: unknown } | undefined;
    if (data && typeof data.error === 'string' && data.error.trim().length > 0) {
      return data.error;
    }
  }
  return fallback;
};
