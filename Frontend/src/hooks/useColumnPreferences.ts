import { useCallback, useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { GridColDef, GridColumnVisibilityModel, GridValidRowModel } from '@mui/x-data-grid';
import listViewService, {
  type ListViewColumnsResponse,
  type ListViewKey,
  type ResolvedColumn,
  type StoredColumn,
} from '../api/services/listViewService';

/**
 * AA-01 · one hook, every grid.
 *
 * The server owns the answer to "which columns, in what order, for this user". This hook
 * does three things with that answer:
 *
 *   1. exposes it for the column picker (visible/hidden, order, reset),
 *   2. reorders the page's own `GridColDef[]` to match, and
 *   3. materialises tenant-defined custom fields as real grid columns.
 *
 * Failure is non-fatal by design. If the preferences endpoint is unreachable, `columns` is
 * empty and `arrangeColumns` returns the page's definitions untouched — the grid renders
 * exactly as it would have without this feature. A preferences outage must never take a
 * working list view offline.
 */

const CUSTOM_FIELD_PREFIX = 'cf:';

/** Reads one custom-field value out of a row's raw jsonb bag. Never throws. */
export const readCustomFieldValue = (bag: unknown, stableKey: string): string => {
  if (typeof bag !== 'string' || bag.trim() === '') return '';
  try {
    const parsed: unknown = JSON.parse(bag);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return '';
    const value = (parsed as Record<string, unknown>)[stableKey];
    if (value === null || value === undefined) return '';
    if (typeof value === 'boolean') return value ? 'Yes' : 'No';
    if (Array.isArray(value)) return value.map((x) => String(x)).join(', ');
    if (typeof value === 'object') return JSON.stringify(value);
    return String(value);
  } catch {
    // The column is open jsonb; anything ever written into it must read as blank, not crash.
    return '';
  }
};

export interface UseColumnPreferencesOptions {
  /**
   * Where a custom-field value lives on a grid row. Defaults to `customFields`, the property
   * the Customer list payload carries.
   */
  customFieldBagField?: string;
  /** Whether to load preferences at all (e.g. gate on a permission). Defaults to true. */
  enabled?: boolean;
}

export interface UseColumnPreferencesResult {
  /** Resolved server metadata for the picker, in display order. Empty while loading or on error. */
  columns: ResolvedColumn[];
  isLoading: boolean;
  isError: boolean;
  /** True when this user has a saved layout — controls whether Reset is offered. */
  isCustomised: boolean;
  /**
   * False when this view has no custom-field attach point, so the picker must not promise
   * that tenant-defined fields will show up here. An empty "Custom" section with no
   * explanation reads as a bug.
   */
  supportsCustomFields: boolean;
  isSaving: boolean;
  setVisible: (key: string, visible: boolean) => void;
  /** Moves a column one slot earlier (-1) or later (+1). No-op at the ends. */
  move: (key: string, direction: -1 | 1) => void;
  reset: () => void;
  /** MUI column visibility model derived from the resolved columns. */
  columnVisibilityModel: GridColumnVisibilityModel;
  /** Persists changes made through the grid's own column menu. */
  onColumnVisibilityModelChange: (model: GridColumnVisibilityModel) => void;
  /**
   * Reorders the page's definitions to the user's order and appends a generated column for
   * each tenant-defined custom field. Definitions the server does not know about are kept at
   * the end rather than dropped, so a grid is never silently emptied by a stale catalog.
   */
  arrangeColumns: <R extends GridValidRowModel>(defs: GridColDef<R>[]) => GridColDef<R>[];
}

export const useColumnPreferences = (
  viewKey: ListViewKey,
  options: UseColumnPreferencesOptions = {},
): UseColumnPreferencesResult => {
  const { customFieldBagField = 'customFields', enabled = true } = options;
  const queryClient = useQueryClient();
  const queryKey = useMemo(() => ['list-view-columns', viewKey], [viewKey]);

  const { data, isPending, isError } = useQuery({
    queryKey,
    queryFn: () => listViewService.getColumns(viewKey),
    enabled,
    // Column layout changes rarely and only by this user's own action; the mutations below
    // write the response straight into the cache, so a long stale time costs nothing.
    staleTime: 5 * 60_000,
    retry: false,
  });

  const columns = data?.columns ?? [];

  const saveMutation = useMutation({
    mutationFn: (next: StoredColumn[]) => listViewService.saveColumns(viewKey, next),
    onSuccess: (response: ListViewColumnsResponse) => queryClient.setQueryData(queryKey, response),
  });

  const resetMutation = useMutation({
    mutationFn: () => listViewService.resetColumns(viewKey),
    onSuccess: (response: ListViewColumnsResponse) => queryClient.setQueryData(queryKey, response),
  });

  const persist = useCallback(
    (next: ResolvedColumn[]) => {
      // Optimistic: the checkbox and the arrows must feel instant. The server response
      // overwrites this on success, and a failure leaves the cache to be refetched.
      queryClient.setQueryData<ListViewColumnsResponse>(queryKey, (current) =>
        current ? { ...current, columns: next, isCustomised: true } : current);
      saveMutation.mutate(next.map((c) => ({ key: c.key, visible: c.visible })));
    },
    [queryClient, queryKey, saveMutation],
  );

  const setVisible = useCallback(
    (key: string, visible: boolean) => {
      if (columns.length === 0) return;
      persist(columns.map((c) => (c.key === key && !c.locked ? { ...c, visible } : c)));
    },
    [columns, persist],
  );

  const move = useCallback(
    (key: string, direction: -1 | 1) => {
      const index = columns.findIndex((c) => c.key === key);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= columns.length) return;
      const next = [...columns];
      [next[index], next[target]] = [next[target], next[index]];
      persist(next);
    },
    [columns, persist],
  );

  const reset = useCallback(() => resetMutation.mutate(), [resetMutation]);

  const columnVisibilityModel = useMemo<GridColumnVisibilityModel>(() => {
    const model: GridColumnVisibilityModel = {};
    for (const column of columns) model[column.key] = column.visible || column.locked;
    return model;
  }, [columns]);

  const onColumnVisibilityModelChange = useCallback(
    (model: GridColumnVisibilityModel) => {
      if (columns.length === 0) return;
      persist(columns.map((c) => ({
        ...c,
        visible: c.locked ? true : model[c.key] !== false,
      })));
    },
    [columns, persist],
  );

  const arrangeColumns = useCallback(
    <R extends GridValidRowModel>(defs: GridColDef<R>[]): GridColDef<R>[] => {
      if (columns.length === 0) return defs;

      const byField = new Map(defs.map((d) => [d.field, d]));
      const ordered: GridColDef<R>[] = [];
      const placed = new Set<string>();

      for (const column of columns) {
        if (column.key.startsWith(CUSTOM_FIELD_PREFIX)) {
          const stableKey = column.stableKey ?? column.key.slice(CUSTOM_FIELD_PREFIX.length);
          ordered.push({
            field: column.key,
            headerName: column.label,
            flex: 1,
            minWidth: 140,
            // Custom-field values live in an untyped jsonb bag; sorting and filtering them
            // server-side is not wired, so they are presented as read-only text rather than
            // pretending to support operations the API cannot honour.
            sortable: false,
            filterable: false,
            valueGetter: (_value: unknown, row: R) =>
              readCustomFieldValue((row as Record<string, unknown>)[customFieldBagField], stableKey),
          } as GridColDef<R>);
          placed.add(column.key);
          continue;
        }
        const def = byField.get(column.key);
        if (!def) continue; // Server knows a column this page no longer renders — ignore it.
        ordered.push(def);
        placed.add(column.key);
      }

      // Anything this page renders that the server catalog does not list stays visible at the
      // end. Losing a column because the catalog drifted is worse than an unordered extra.
      for (const def of defs) if (!placed.has(def.field)) ordered.push(def);

      return ordered;
    },
    [columns, customFieldBagField],
  );

  return {
    columns,
    isLoading: enabled && isPending,
    isError,
    isCustomised: data?.isCustomised ?? false,
    supportsCustomFields: data?.supportsCustomFields ?? false,
    isSaving: saveMutation.isPending || resetMutation.isPending,
    setVisible,
    move,
    reset,
    columnVisibilityModel,
    onColumnVisibilityModelChange,
    arrangeColumns,
  };
};

export default useColumnPreferences;
