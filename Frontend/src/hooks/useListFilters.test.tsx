import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import React from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import useListFilters, {
  EMPTY_LIST_FILTERS, aheadRange, dueParams, localToday, parseListFilters, receivedRange,
} from './useListFilters';

const renderAt = (route: string) => {
  const location = { current: '' };
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter initialEntries={[route]}>
      <LocationSpy onChange={(search) => { location.current = search; }} />
      {children}
    </MemoryRouter>
  );
  const hook = renderHook(() => useListFilters(), { wrapper });
  return { ...hook, location };
};

const LocationSpy: React.FC<{ onChange: (search: string) => void }> = ({ onChange }) => {
  const { search } = useLocation();
  onChange(search);
  return null;
};

describe('parseListFilters', () => {
  it('reads valid values', () => {
    expect(parseListFilters(new URLSearchParams('customer=30&due=overdue&rep=42')))
      .toEqual({ ...EMPTY_LIST_FILTERS, customer: '30', due: 'overdue', rep: 42 });
    expect(parseListFilters(new URLSearchParams('customer=none&due=14d'))).toEqual({ ...EMPTY_LIST_FILTERS, customer: 'none', due: '14d' });
  });

  it('reads the column filters: text, days, counts, status, source and owner', () => {
    expect(parseListFilters(new URLSearchParams(
      'serial=%20NOOR-59%20&rfq=6000&buyer=ali%40x.com&agreement=AG-1&dueFrom=2026-09-01&dueTo=2026-09-30'
      + '&receivedFrom=2026-08-01&requiredTo=2026-12-31&itemsMin=0&itemsMax=10&status=none&source=Email&owner=mine',
    ))).toEqual({
      ...EMPTY_LIST_FILTERS,
      serial: 'NOOR-59', rfq: '6000', buyer: 'ali@x.com', agreement: 'AG-1',
      dueFrom: '2026-09-01', dueTo: '2026-09-30', receivedFrom: '2026-08-01', requiredTo: '2026-12-31',
      itemsMin: 0, itemsMax: 10, status: 'none', source: 'Email', owner: 'mine',
    });
    expect(parseListFilters(new URLSearchParams('status=12&received=7d&required=30d')))
      .toEqual({ ...EMPTY_LIST_FILTERS, status: '12', received: '7d', required: '30d' });
  });

  it('turns junk into the defaults', () => {
    expect(parseListFilters(new URLSearchParams('customer=abc&due=tomorrow&rep=-3')))
      .toEqual(EMPTY_LIST_FILTERS);
    expect(parseListFilters(new URLSearchParams('customer=0&due=&rep=1.5'))).toEqual(EMPTY_LIST_FILTERS);
    expect(parseListFilters(new URLSearchParams(''))).toEqual(EMPTY_LIST_FILTERS);
    expect(parseListFilters(new URLSearchParams(
      'dueFrom=2026-02-30&dueTo=30/09/2026&itemsMin=-1&itemsMax=ten&status=open&source=%3Cscript%3E&owner=boss'
      + `&received=week&required=90d&serial=${'x'.repeat(201)}`,
    ))).toEqual(EMPTY_LIST_FILTERS);
  });

  it('lets a preset win over a custom range for the same date, and Unassigned/Mine drop a rep', () => {
    expect(parseListFilters(new URLSearchParams('due=7d&dueFrom=2026-09-01&dueTo=2026-09-30')))
      .toEqual({ ...EMPTY_LIST_FILTERS, due: '7d' });
    expect(parseListFilters(new URLSearchParams('received=today&receivedFrom=2026-09-01')))
      .toEqual({ ...EMPTY_LIST_FILTERS, received: 'today' });
    expect(parseListFilters(new URLSearchParams('owner=unassigned&rep=42')))
      .toEqual({ ...EMPTY_LIST_FILTERS, owner: 'unassigned' });
  });
});

describe('useListFilters', () => {
  it('exposes the URL values and whether any narrows the list', () => {
    const { result } = renderAt('/list?customer=30&due=7d');
    expect(result.current.customer).toBe('30');
    expect(result.current.due).toBe('7d');
    expect(result.current.rep).toBeNull();
    expect(result.current.active).toBe(true);
    expect(renderAt('/list?buyer=ali').result.current.active).toBe(true);
    expect(renderAt('/list?status=none').result.current.active).toBe(true);
  });

  it('is not active with nothing or only junk on the URL', () => {
    expect(renderAt('/list?due=soon').result.current.active).toBe(false);
  });

  it('writes only non-default values and keeps every other key', () => {
    const { result, location } = renderAt('/list?view=revisions&search=REF-1');
    act(() => result.current.set({ customer: '30', due: '14d', rep: 7 }));
    const written = new URLSearchParams(location.current);
    expect(written.get('view')).toBe('revisions');
    expect(written.get('search')).toBe('REF-1');
    expect(written.get('customer')).toBe('30');
    expect(written.get('due')).toBe('14d');
    expect(written.get('rep')).toBe('7');

    act(() => result.current.set({ due: null, rep: null }));
    const after = new URLSearchParams(location.current);
    expect(after.has('due')).toBe(false);
    expect(after.has('rep')).toBe(false);
    expect(after.get('customer')).toBe('30');
    expect(after.get('view')).toBe('revisions');
  });

  it('writes text, days and counts, and removes a cleared one', () => {
    const { result, location } = renderAt('/list');
    act(() => result.current.set({ serial: ' NOOR ', dueFrom: '2026-09-01', itemsMin: 0, owner: 'mine' }));
    const written = new URLSearchParams(location.current);
    expect(written.get('serial')).toBe('NOOR');
    expect(written.get('dueFrom')).toBe('2026-09-01');
    expect(written.get('itemsMin')).toBe('0');
    expect(written.get('owner')).toBe('mine');
    act(() => result.current.set({ serial: null, itemsMin: null }));
    const after = new URLSearchParams(location.current);
    expect(after.has('serial')).toBe(false);
    expect(after.has('itemsMin')).toBe(false);
    expect(after.get('dueFrom')).toBe('2026-09-01');
  });

  it('clear removes only its own keys', () => {
    const { result, location } = renderAt('/list?view=revisions&customer=none&due=overdue&rep=3&state=x&serial=A&status=none&source=Email&owner=mine&itemsMax=10');
    act(() => result.current.clear());
    const after = new URLSearchParams(location.current);
    expect([...after.keys()].sort()).toEqual(['state', 'view']);
    expect(result.current.active).toBe(false);
  });

  it('dueParams sends today on the local calendar with a window and nothing for Any', () => {
    const { result } = renderAt('/list?due=overdue');
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
    expect(result.current.dueParams()).toEqual({ due: 'overdue', today });
    expect(renderAt('/list').result.current.dueParams()).toEqual({});
  });
});

describe('localToday / dueParams', () => {
  it('uses the local calendar day, not the UTC one', () => {
    // 23:30 local on 3 March: the UTC date may already be the 4th, the reader's day is the 3rd.
    const lateEvening = new Date(2026, 2, 3, 23, 30);
    expect(localToday(lateEvening)).toBe('2026-03-03');
    expect(dueParams('7d', lateEvening)).toEqual({ due: '7d', today: '2026-03-03' });
    expect(dueParams(null, lateEvening)).toEqual({});
  });

  it('turns Received and Required presets into local day ranges, across a month end', () => {
    const now = new Date(2026, 2, 3, 23, 30);
    expect(receivedRange('today', now)).toEqual({ from: '2026-03-03', to: '2026-03-03' });
    expect(receivedRange('7d', now)).toEqual({ from: '2026-02-25', to: '2026-03-03' });
    expect(receivedRange('30d', now)).toEqual({ from: '2026-02-02', to: '2026-03-03' });
    expect(aheadRange('30d', now)).toEqual({ from: '2026-03-03', to: '2026-04-02' });
  });
});
