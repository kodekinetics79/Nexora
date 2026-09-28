import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import listViewService, { type ListViewColumnsResponse, type ListViewKey, type StoredColumn } from '../../../api/services/listViewService';

/**
 * The reader's own dashboard: which bands they keep, in what order, and what each chart counts.
 *
 * Both live in the per-user list-view store, so a choice follows the reader to any browser. The
 * store holds an ordered list of keys with a visible flag, which is exactly both shapes:
 *  - `dashboard.layout` — one key per band; order is the grid order, visible is shown/hidden.
 *  - `dashboard.charts` — one key per chart option, `<chart>.<option>`; the visible one in each
 *    chart's group is that chart's current choice.
 * Defaults live in ListViewCatalog on the server. When the store cannot be reached the screen
 * still works on the defaults below; a choice made then lasts until the page reloads.
 */

const useStoredView = (viewKey: ListViewKey) => {
  const client = useQueryClient();
  const key = ['list-views', viewKey];
  const stored = useQuery({
    queryKey: key,
    queryFn: () => listViewService.getColumns(viewKey),
    staleTime: Infinity,
    retry: 0,
    meta: { silenceGlobalError: true },
  });
  const save = useMutation({
    mutationFn: (columns: StoredColumn[]) => listViewService.saveColumns(viewKey, columns),
    onMutate: (columns) => {
      // Optimistic: the screen moves the moment the reader chooses, not when the server answers.
      const previous = client.getQueryData<ListViewColumnsResponse>(key);
      client.setQueryData<ListViewColumnsResponse>(key, {
        viewKey, isCustomised: true, supportsCustomFields: false,
        columns: columns.map(c => ({ ...c, label: c.key, locked: false, source: 'catalog' as const })),
      });
      return { previous };
    },
    onSuccess: data => client.setQueryData(key, data),
  });
  return { columns: stored.data?.columns ?? null, save: (c: StoredColumn[]) => save.mutate(c) };
};

/** One chart's current option, and a setter that saves it. */
export function useChartChoice<T extends string>(chart: string, options: readonly T[], fallback: T) {
  const { columns, save } = useStoredView('dashboard.charts');
  const chosen = columns?.find(c => c.visible && c.key.startsWith(`${chart}.`))?.key.slice(chart.length + 1);
  const value = (options as readonly string[]).includes(chosen ?? '') ? (chosen as T) : fallback;
  const choose = (next: T) => {
    const others = (columns ?? []).filter(c => !c.key.startsWith(`${chart}.`)).map(c => ({ key: c.key, visible: c.visible }));
    save([...others, ...options.map(o => ({ key: `${chart}.${o}`, visible: o === next }))]);
  };
  return [value, choose] as const;
}

export type BandKey = 'verdict' | 'outstanding' | 'losses' | 'closing' | 'today' | 'sixmonths' | 'brands' | 'customers';

export const BAND_KEYS: readonly BandKey[] = ['verdict', 'outstanding', 'losses', 'closing', 'today', 'sixmonths', 'brands', 'customers'];

export const BAND_LABELS: Readonly<Record<BandKey, string>> = {
  verdict: 'Did we win what we decided?',
  outstanding: "What's out with customers",
  losses: 'Why we lost',
  closing: "What's closing on us",
  today: 'What needs you today',
  sixmonths: 'The last six months',
  brands: 'What customers ask for',
  customers: 'Who we quote',
};

/** The reader's band order and which bands they hide. */
export function useBandLayout() {
  const { columns, save } = useStoredView('dashboard.layout');
  const known = (columns ?? []).filter(c => (BAND_KEYS as readonly string[]).includes(c.key));
  // Any band the store does not mention (a new band, or an unreachable store) shows, at the end.
  const order: { key: BandKey; visible: boolean }[] = [
    ...known.map(c => ({ key: c.key as BandKey, visible: c.visible })),
    ...BAND_KEYS.filter(k => !known.some(c => c.key === k)).map(key => ({ key, visible: true })),
  ];
  return { order, save: (next: { key: BandKey; visible: boolean }[]) => save(next) };
}
