import axiosInstance from '../axiosInstance';

/**
 * FR-DSH-04 — the top bar's cross-entity quick search.
 *
 * This replaces a ten-entry keyword→route table that lived in `Navbar.tsx`, made no network
 * request, and — on no match — navigated silently to `/dashboard`. A search for a customer that
 * does not exist looked exactly like a search for one that does, which is the wiring contract's
 * failure #7: a control that reports success while doing nothing.
 */

export type SearchEntity =
  | 'customer'
  | 'supplier'
  | 'product'
  | 'lead'
  | 'rfq'
  | 'quote'
  | 'order'
  | 'shipment';

export interface SearchHit {
  entity: SearchEntity;
  id: number;
  title: string;
  /** Secondary identification. Null when the record genuinely carries none. */
  subtitle?: string | null;
  /** Null when the family has no status concept — rendered as nothing, never as "Unknown". */
  status?: string | null;
  occurredOn?: string | null;
  /** The column the date filter matched on. The families do not share one. */
  dateField: string;
  /** Which field the term hit, so an unexpected-looking result can be explained. */
  matchedOn: string;
}

export interface GlobalSearchResponse {
  query: string;
  hits: SearchHit[];
  searchedEntities: SearchEntity[];
  /** Families the caller asked for and may not read. Stated, never silently dropped. */
  deniedEntities: SearchEntity[];
  /** Families where the limit was reached, so a count is not mistaken for a total. */
  truncated: SearchEntity[];
  /** Stated gaps, e.g. "customers carry no status column, so the status filter excluded them". */
  notes: string[];
}

export interface GlobalSearchParams {
  q: string;
  entities?: SearchEntity[];
  /** Inclusive lower bound, ISO. */
  from?: string;
  /** EXCLUSIVE upper bound, ISO — [from, to), the convention used everywhere in this product. */
  to?: string;
  status?: string;
  limit?: number;
}

/** Server-enforced too; stated here so the box can refuse before spending a round trip. */
export const MIN_SEARCH_LENGTH = 2;

/** The route a hit opens. Every family resolves to a real screen — there is no default. */
export const routeForHit = (hit: SearchHit): string => {
  switch (hit.entity) {
    case 'customer':
      return `/customers/${hit.id}`;
    case 'supplier':
      return `/suppliers/${hit.id}`;
    case 'product':
      return `/inventory/products/${hit.id}`;
    case 'lead':
      return `/procurement/leads/view/${hit.id}`;
    case 'rfq':
      return `/procurement/rfqs/view/${hit.id}`;
    case 'quote':
      return `/sales/quotes/view/${hit.id}`;
    case 'order':
      return `/sales/orders/${hit.id}`;
    case 'shipment':
      return `/sales/shipments/${hit.id}`;
  }
};

export const ENTITY_LABELS: Record<SearchEntity, string> = {
  customer: 'Customer',
  supplier: 'Supplier',
  product: 'Product',
  lead: 'Enquiry',
  rfq: 'RFQ',
  quote: 'Quote',
  order: 'Order',
  shipment: 'Shipment',
};

const searchService = {
  /**
   * Runs the search. Errors are NOT swallowed into an empty result: a failed request and a
   * genuine "nothing matched" must not look the same, which is the defect this endpoint exists
   * to end. The caller renders the server's own message verbatim.
   */
  search: async (
    params: GlobalSearchParams,
    signal?: AbortSignal,
  ): Promise<GlobalSearchResponse> => {
    const response = await axiosInstance.get('/api/search', {
      params: {
        q: params.q,
        entities: params.entities?.length ? params.entities.join(',') : undefined,
        from: params.from,
        to: params.to,
        status: params.status,
        limit: params.limit,
      },
      signal,
    });
    const data = response.data as Partial<GlobalSearchResponse> | null | undefined;
    // A successful HTTP status with an empty or stale response contract used to leave the
    // combobox expanded with a blank panel: neither results, no-match nor error. Refuse that
    // impossible state here so the UI can state the outage and offer a retry.
    if (!data || typeof data.query !== 'string'
      || !Array.isArray(data.hits)
      || !Array.isArray(data.searchedEntities)
      || !Array.isArray(data.deniedEntities)
      || !Array.isArray(data.truncated)
      || !Array.isArray(data.notes)) {
      throw new Error('Search returned an unreadable response.');
    }
    return data as GlobalSearchResponse;
  },
};

export default searchService;
