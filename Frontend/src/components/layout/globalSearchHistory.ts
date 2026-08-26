import type { SearchHit } from '../../api/services/searchService';

export const MAX_RECENT_SEARCH_RECORDS = 5;

export interface SearchHistoryScope {
  businessUnitId?: number;
  userId?: number;
}

const keyFor = ({ businessUnitId, userId }: SearchHistoryScope) =>
  businessUnitId && userId
    ? `nexora:global-search:recent:v1:${businessUnitId}:${userId}`
    : null;

const isHit = (value: unknown): value is SearchHit => {
  if (!value || typeof value !== 'object') return false;
  const hit = value as Partial<SearchHit>;
  return typeof hit.entity === 'string'
    && typeof hit.id === 'number'
    && Number.isFinite(hit.id)
    && typeof hit.title === 'string'
    && typeof hit.dateField === 'string'
    && typeof hit.matchedOn === 'string';
};

/**
 * Session-scoped, tenant-and-user-scoped record history. We retain only records the person
 * actually opened, never raw query text (which can contain customer names, emails or IDs).
 */
export const loadRecentSearchHits = (
  scope: SearchHistoryScope,
  storage: Pick<Storage, 'getItem' | 'removeItem'> = sessionStorage,
): SearchHit[] => {
  const key = keyFor(scope);
  if (!key) return [];
  try {
    const parsed = JSON.parse(storage.getItem(key) ?? '[]') as unknown;
    if (!Array.isArray(parsed)) throw new Error('History is not an array.');
    return parsed.filter(isHit).slice(0, MAX_RECENT_SEARCH_RECORDS);
  } catch {
    storage.removeItem(key);
    return [];
  }
};

export const rememberSearchHit = (
  scope: SearchHistoryScope,
  hit: SearchHit,
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> = sessionStorage,
): SearchHit[] => {
  const key = keyFor(scope);
  if (!key || !isHit(hit)) return [];
  const next = [hit, ...loadRecentSearchHits(scope, storage)
    .filter(item => item.entity !== hit.entity || item.id !== hit.id)]
    .slice(0, MAX_RECENT_SEARCH_RECORDS);
  storage.setItem(key, JSON.stringify(next));
  return next;
};

export const clearRecentSearchHits = (
  scope: SearchHistoryScope,
  storage: Pick<Storage, 'removeItem'> = sessionStorage,
) => {
  const key = keyFor(scope);
  if (key) storage.removeItem(key);
};
