import { beforeEach, describe, expect, it } from 'vitest';
import type { SearchHit } from '../../api/services/searchService';
import {
  clearRecentSearchHits,
  loadRecentSearchHits,
  rememberSearchHit,
} from './globalSearchHistory';

const hit = (id: number, title = `RFQ-${id}`): SearchHit => ({
  entity: 'rfq',
  id,
  title,
  dateField: 'createdOn',
  matchedOn: 'rfq number',
});

beforeEach(() => sessionStorage.clear());

describe('global search recent records', () => {
  it('is tenant-and-user scoped, de-duplicates opened records, and clears only the active scope', () => {
    const active = { businessUnitId: 42, userId: 7 };
    const otherTenant = { businessUnitId: 99, userId: 7 };

    rememberSearchHit(active, hit(62, 'First title'));
    rememberSearchHit(active, hit(62, 'Latest title'));
    rememberSearchHit(otherTenant, hit(99));

    expect(loadRecentSearchHits(active)).toEqual([hit(62, 'Latest title')]);
    expect(loadRecentSearchHits(otherTenant)).toEqual([hit(99)]);

    clearRecentSearchHits(active);

    expect(loadRecentSearchHits(active)).toEqual([]);
    expect(loadRecentSearchHits(otherTenant)).toEqual([hit(99)]);
  });
});
