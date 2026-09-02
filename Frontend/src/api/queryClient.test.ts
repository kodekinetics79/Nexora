import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The floor under every read and write.
 *
 * Before this, `main.tsx` built a QueryClient with `defaultOptions` only — no `queryCache.onError`
 * and no `mutationCache.onError`. Correct handling was opt-in per screen, and:
 *
 *  - 19 list screens destructured `useQuery` without `isError`, so a 500 rendered the grid's empty
 *    state. An outage and an empty tenant were the same pixels.
 *  - 35 of 174 mutations had no `onError`, so a rejected save produced no message at all. On the
 *    customer-PO cancel path the dialog closes before the request resolves and `expectedVersion`
 *    is sent, making a 409 the EXPECTED failure — the user believed a live PO was cancelled.
 *
 * These tests pin the floor itself rather than any one screen, because the defect was the absence
 * of a floor.
 */

// vi.mock factories are hoisted above the import block, so the spy cannot be a plain top-level
// const — it would be referenced before initialisation. vi.hoisted lifts it with the mock.
const { toastError } = vi.hoisted(() => ({ toastError: vi.fn() }));
vi.mock('react-hot-toast', () => ({
  default: { error: toastError },
  toast: { error: toastError },
}));

import { queryClient } from './queryClient';

/** An axios-shaped rejection, which is what every caller in this product actually throws. */
const axiosError = (status: number, data: unknown = 'Upstream failed.') => ({
  isAxiosError: true,
  response: { status, data },
  message: `Request failed with status code ${status}`,
});

beforeEach(() => {
  toastError.mockClear();
  queryClient.clear();
});

describe('global query failure reporting', () => {
  it('reports a failed read and names the view from the query key', async () => {
    await queryClient
      .fetchQuery({
        queryKey: ['shipments'],
        queryFn: () => Promise.reject(axiosError(500)),
        retry: false,
      })
      .catch(() => {});

    expect(toastError).toHaveBeenCalledTimes(1);
    expect(toastError.mock.calls[0][0]).toContain("Couldn't load Shipments");
  });

  it('humanises a kebab-case query key rather than showing the key', async () => {
    await queryClient
      .fetchQuery({
        queryKey: ['supplier-purchase-orders', 7],
        queryFn: () => Promise.reject(axiosError(503)),
        retry: false,
      })
      .catch(() => {});

    expect(toastError.mock.calls[0][0]).toContain("Couldn't load Supplier purchase orders");
  });

  it('collapses repeated failures of one view into a single toast id', async () => {
    for (const _ of [1, 2, 3]) {
      await queryClient
        .fetchQuery({
          queryKey: ['shipments'],
          queryFn: () => Promise.reject(axiosError(500)),
          retry: false,
        })
        .catch(() => {});
      queryClient.clear();
    }

    const ids = toastError.mock.calls.map((call) => call[1]?.id);
    expect(new Set(ids).size).toBe(1);
    // Keyed on the screen (route), not the query key — see the per-screen tests below.
    expect(ids[0]).toBe('query-error:/');
  });

  it('stays quiet on 401, because the axios interceptor owns the redirect to /login', async () => {
    await queryClient
      .fetchQuery({
        queryKey: ['quotes'],
        queryFn: () => Promise.reject(axiosError(401)),
        retry: false,
      })
      .catch(() => {});

    expect(toastError).not.toHaveBeenCalled();
  });

  it('stays quiet when the caller renders its own failure UI', async () => {
    await queryClient
      .fetchQuery({
        queryKey: ['quotes'],
        queryFn: () => Promise.reject(axiosError(500)),
        retry: false,
        meta: { silenceGlobalError: true },
      })
      .catch(() => {});

    expect(toastError).not.toHaveBeenCalled();
  });
});

describe('global write failure reporting', () => {
  it('reports a rejected save that the calling screen does not handle', async () => {
    // The exact shape of the customer-PO cancel: a version conflict on a destructive action,
    // with no onError on the mutation.
    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: () => Promise.reject(axiosError(409)) })
      .execute(undefined)
      .catch(() => {});

    expect(toastError).toHaveBeenCalledTimes(1);
  });

  it('never renders a raw non-string server body at a user', async () => {
    await queryClient
      .getMutationCache()
      .build(queryClient, {
        mutationFn: () => Promise.reject(axiosError(400, { errors: { Qty: ['Required'] } })),
      })
      .execute(undefined)
      .catch(() => {});

    const message = toastError.mock.calls[0][0] as string;
    expect(message).not.toContain('[object Object]');
    expect(message.length).toBeGreaterThan(0);
  });

  it('stays out of the way when the caller handles its own failure', async () => {
    // This test originally asserted the OPPOSITE — that the backstop fires alongside a caller's
    // own handler. That was wrong, and only measuring the real codebase showed it: 95 files
    // declare a local onError and 152 of those handlers raise a toast within a few lines. Firing
    // here too would have double-reported every one of them, and the opt-out this file first
    // relied on (meta.silenceGlobalError) was declared by exactly zero files.
    //
    // A backstop reports what nothing else reports. A declared onError IS something else.
    const localHandler = vi.fn();
    await queryClient
      .getMutationCache()
      .build(queryClient, {
        mutationFn: () => Promise.reject(axiosError(500)),
        onError: localHandler,
      })
      .execute(undefined)
      .catch(() => {});

    expect(localHandler).toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });

  it('still reports a caller that declares no handler at all', async () => {
    // The 35 mutations this whole change exists for.
    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: () => Promise.reject(axiosError(500)) })
      .execute(undefined)
      .catch(() => {});

    expect(toastError).toHaveBeenCalledTimes(1);
  });

  it('respects an explicit opt-out on a mutation', async () => {
    await queryClient
      .getMutationCache()
      .build(queryClient, {
        mutationFn: () => Promise.reject(axiosError(500)),
        meta: { silenceGlobalError: true },
      })
      .execute(undefined)
      .catch(() => {});

    expect(toastError).not.toHaveBeenCalled();
  });
});

describe('one toast per screen, not per query', () => {
  it('collapses failures of different queries on the same route into one toast id', async () => {
    // A screen that reads five endpoints used to stack five red toasts beside its own inline
    // panels. Two keys, one route, one toast.
    for (const key of [['inbox', 'needs-review'], ['inbox', 'blocked'], ['lead-decision-summaries']]) {
      await queryClient
        .fetchQuery({ queryKey: key, queryFn: () => Promise.reject(axiosError(404, 'GET /api/Lead/needs-review')), retry: false })
        .catch(() => {});
    }

    const ids = new Set(toastError.mock.calls.map((call) => call[1]?.id));
    expect(ids.size).toBe(1);
  });

  it('never puts an API path in the toast', async () => {
    await queryClient
      .fetchQuery({ queryKey: ['inbox', 'needs-review'], queryFn: () => Promise.reject(axiosError(404, 'GET /api/Lead/needs-review')), retry: false })
      .catch(() => {});

    const message = toastError.mock.calls[0][0] as string;
    expect(message).not.toContain('/api/');
    // Nor the record-404 wording: a list has no identity to be "no longer existing".
    expect(message).not.toContain('no longer exists');
  });
});
