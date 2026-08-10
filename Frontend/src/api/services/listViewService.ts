import axiosInstance from '../axiosInstance';

/**
 * AA-01 · per-user list-view column preferences.
 *
 * Every call is scoped server-side to the acting user and business unit from the token —
 * there is deliberately no user id in any signature here, because a client that can name a
 * user is a client that can read someone else's layout.
 */

/** Keys must match ListViewCatalog on the server. A typo yields a 404, not a silent default. */
export type ListViewKey =
  | 'leads.list'
  | 'customers.list'
  | 'suppliers.list'
  | 'lead.items';

export type CustomFieldDataType =
  | 'Text' | 'Integer' | 'Decimal' | 'Boolean' | 'Date'
  | 'Timestamp' | 'Option' | 'MultiOption' | 'Json' | 'Reference';

export interface ResolvedColumn {
  /** Grid field name. Tenant-defined custom fields are prefixed `cf:`. */
  key: string;
  label: string;
  visible: boolean;
  /** Cannot be hidden; may still be reordered. */
  locked: boolean;
  source: 'catalog' | 'customField';
  dataType?: CustomFieldDataType | null;
  /** Custom-field stable key without the `cf:` prefix. Null for catalog columns. */
  stableKey?: string | null;
}

export interface ListViewColumnsResponse {
  viewKey: string;
  columns: ResolvedColumn[];
  /** True when this user has saved a layout — drives whether "Reset to default" is offered. */
  isCustomised: boolean;
  /** False when the view has no custom-field attach point — see ListViewCatalog. */
  supportsCustomFields: boolean;
}

export interface StoredColumn {
  key: string;
  visible: boolean;
}

const listViewService = {
  getColumns: async (viewKey: ListViewKey): Promise<ListViewColumnsResponse> => {
    const r = await axiosInstance.get(`/api/list-views/${encodeURIComponent(viewKey)}/columns`);
    return r.data;
  },

  /** Replaces the acting user's layout. Order of `columns` IS the display order. */
  saveColumns: async (viewKey: ListViewKey, columns: StoredColumn[]): Promise<ListViewColumnsResponse> => {
    const r = await axiosInstance.put(`/api/list-views/${encodeURIComponent(viewKey)}/columns`, { columns });
    return r.data;
  },

  resetColumns: async (viewKey: ListViewKey): Promise<ListViewColumnsResponse> => {
    const r = await axiosInstance.delete(`/api/list-views/${encodeURIComponent(viewKey)}/columns`);
    return r.data;
  },
};

export default listViewService;
