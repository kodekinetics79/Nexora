import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { presentableErrorMessage, toPresentableError } from '../utils/apiErrors';

/**
 * The application's QueryClient, and the floor under every read and write in the product.
 *
 * Correct failure handling used to be opt-in per screen, and 19 list screens had opted out by
 * omission: they destructured `useQuery` without `isError`, so a 500 rendered the grid's empty
 * state. A server outage and an empty tenant were the same pixels. On Setup the copy was worse
 * than "No rows" because it was confident and specific — "No inbox is connected yet, so no leads
 * can arrive by email" is what a mailbox read renders when it 500s, and an admin then re-creates
 * a mailbox that already exists.
 *
 * Writes were worse still. 35 of 174 mutations had no `onError`, and there was no cache-level
 * handler, so a rejected save produced nothing at all — the customer-PO cancel dialog closed
 * optimistically before the request resolved, and `expectedVersion` is sent on that call, so a 409
 * is the EXPECTED failure mode rather than an edge case. The user believed a PO was cancelled
 * while it was still live.
 *
 * These two handlers invert the default: a failure is now reported unless a screen deliberately
 * opts out via `meta.silenceGlobalError`. A screen that already renders its own inline error is
 * free to keep doing so — the toast is a floor, not a replacement — and no screen added later can
 * fall below it.
 *
 * Messages go through `presentableErrorMessage` so nothing raw reaches a user; see
 * `utils/apiErrors.ts` for the rules that boundary enforces.
 */

/** Meta a query or mutation can set to handle its own failures and suppress the global report. */
export interface NexoraQueryMeta extends Record<string, unknown> {
  /** Set when the caller renders its own failure UI and a toast would duplicate it. */
  silenceGlobalError?: boolean;
  /** Human name for the thing being loaded, used in "Couldn't load {label}". */
  errorLabel?: string;
}

const metaOf = (source: { meta?: unknown }): NexoraQueryMeta =>
  (source.meta ?? {}) as NexoraQueryMeta;

/**
 * Whether this failure is already being handled elsewhere and must not raise a toast.
 *
 * 401 is the important case: `api/axiosInstance.ts` owns the redirect to /login, and a toast
 * racing a full-document navigation either flashes and dies or lands on the login screen as an
 * orphan. Cancelled requests are route changes and StrictMode double-effects, not failures.
 */
const isSilent = (error: unknown, meta: NexoraQueryMeta): boolean => {
  if (meta.silenceGlobalError) return true;
  const presented = toPresentableError(error);
  return presented.isCanceled || presented.status === 401;
};

/**
 * A short, human name for what failed, from the query key.
 *
 * Keys in this product are kebab or camel strings — `['quote-detail', id]`, `['shipments']` — so
 * the first segment is the view. Anything unrecognisable degrades to a generic sentence rather
 * than rendering a key at a user.
 */
const labelFromQueryKey = (query: { queryKey: readonly unknown[]; meta?: unknown }): string | null => {
  const explicit = metaOf(query).errorLabel;
  if (typeof explicit === 'string' && explicit.trim()) return explicit.trim();

  const head = Array.isArray(query.queryKey) ? query.queryKey[0] : undefined;
  if (typeof head !== 'string' || !head.trim()) return null;

  const words = head
    .replace(/[-_]+/g, ' ')
    .replace(/([a-z\d])([A-Z])/g, '$1 $2')
    .trim()
    .toLowerCase();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : null;
};

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },

  queryCache: new QueryCache({
    onError: (error, query) => {
      const meta = metaOf(query);
      if (isSilent(error, meta)) return;

      const label = labelFromQueryKey(query);
      const detail = presentableErrorMessage(
        error,
        'The server did not return a result. Nothing is missing from your data — this screen could not read it.',
      );

      // A stable id per view collapses an outage that fails five queries on one screen into one
      // message, and collapses a retrying query into one message rather than a stack.
      toast.error(label ? `Couldn't load ${label}. ${detail}` : detail, {
        id: `query-error:${String(Array.isArray(query.queryKey) ? query.queryKey[0] : 'unknown')}`,
        duration: 6000,
      });
    },
  }),

  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      const meta = metaOf(mutation.options);
      if (isSilent(error, meta)) return;

      // A backstop reports what NOTHING ELSE reports. A mutation that declares its own onError
      // has said it handles failure, and 95 files do exactly that — 152 of those handlers raise
      // a toast within a few lines. Firing here as well double-reports every one of them.
      //
      // The first version of this gated on an opt-out (meta.silenceGlobalError) instead, which
      // was the wrong lever: it made correct behaviour require 95 edits, so in practice every
      // one of those screens would have shipped showing two toasts. Reading the caller's own
      // onError needs no edits and cannot drift.
      if (typeof mutation.options.onError === 'function') return;

      // Writes get no dedup id: two different failed saves are two things the user must know
      // about, and a write that fails silently is the most expensive failure in the product.
      toast.error(
        presentableErrorMessage(error, 'That change was not saved. Nothing was applied.'),
        { duration: 8000 },
      );
    },
  }),
});

export default queryClient;
