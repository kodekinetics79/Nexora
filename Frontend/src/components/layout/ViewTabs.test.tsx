import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { NavView } from './navCatalog';

const auth = { permitted: true };

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => auth.permitted }),
}));

import ViewTabs, { activeViewKey } from './ViewTabs';

/**
 * The tab strip inherited the hardest problem the old rail had.
 *
 * Seven rail entries addressed a FILTERED list through a query string, `location.pathname` never
 * carries a query, and the rail compared pathnames — so not one of them could ever highlight. A rep
 * clicked "Sent Quotes", landed on a screen with nothing lit and no filter stated, and read that as
 * an empty pipeline. Those seven are tabs now, so the matching rules had to move here intact:
 *
 *  - a filtered address lights its own tab, not its bare sibling;
 *  - a bare tab stands down when the address carries a filter key one of its siblings claims;
 *  - an unrelated query parameter (?page=2) must not blank the strip;
 *  - a detail page lights the list it was opened from.
 */

const QUOTE_VIEWS: NavView[] = [
  { key: 'draft', label: 'Drafts', path: '/sales/quotes?state=draft', activePrefixes: ['/sales/quotes/view'] },
  { key: 'sent', label: 'Sent', path: '/sales/quotes?state=sent' },
  { key: 'follow-up', label: 'Follow-up due', path: '/sales/quotes?state=follow-up' },
];

const RFQ_VIEWS: NavView[] = [
  { key: 'all', label: 'All RFQs', path: '/procurement/rfqs/all' },
  { key: 'ready', label: 'Ready for quote', path: '/procurement/rfqs/all?state=ready-for-quote' },
  { key: 'draft', label: 'Drafts', path: '/procurement/rfqs/draft' },
];

describe('which tab an address is on', () => {
  it('lights the filtered tab on its own query', () => {
    expect(activeViewKey(QUOTE_VIEWS, '/sales/quotes', '?state=sent')).toBe('sent');
  });

  it('lights only that one', () => {
    expect(activeViewKey(QUOTE_VIEWS, '/sales/quotes', '?state=follow-up')).toBe('follow-up');
  });

  it('stands the bare tab down when a sibling filter key is on the address', () => {
    // "All RFQs" and "Ready for quote" are the same pathname. Lighting "All RFQs" over a narrowed
    // grid tells the reader they are looking at everything while they are not.
    expect(activeViewKey(RFQ_VIEWS, '/procurement/rfqs/all', '?state=ready-for-quote')).toBe('ready');
  });

  it('leaves the bare tab lit for a parameter that is not one of the filters', () => {
    expect(activeViewKey(RFQ_VIEWS, '/procurement/rfqs/all', '?page=2')).toBe('all');
  });

  it('lights a tab that has its own route and no query at all', () => {
    expect(activeViewKey(RFQ_VIEWS, '/procurement/rfqs/draft', '')).toBe('draft');
  });

  it('lights the owning list from a detail page', () => {
    expect(activeViewKey(QUOTE_VIEWS, '/sales/quotes/view/42', '')).toBe('draft');
  });

  it('lights nothing for an address that belongs to no view', () => {
    expect(activeViewKey(QUOTE_VIEWS, '/customers', '')).toBeUndefined();
  });
});

describe('the strip itself', () => {
  beforeEach(() => {
    auth.permitted = true;
  });

  it('renders one tablist and no nested one', () => {
    render(
      <MemoryRouter initialEntries={['/sales/quotes?state=sent']}>
        <ViewTabs primaryKey="quotes" />
      </MemoryRouter>,
    );

    // One level of tabs, always. A second tablist inside the panel is the defect this pass removed
    // from the platform console's Audit tab, and it must not reappear here.
    expect(screen.getAllByRole('tablist')).toHaveLength(1);
    expect(screen.getByRole('tab', { name: 'Sent' })).toHaveAttribute('aria-selected', 'true');
  });

  it('offers the real quote states as tabs rather than as four rail rows', () => {
    render(
      <MemoryRouter initialEntries={['/sales/quotes?state=draft']}>
        <ViewTabs primaryKey="quotes" />
      </MemoryRouter>,
    );

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual([
      'Drafts',
      'Sent',
      'Follow-up due',
      'Won / lost',
    ]);
  });

  it('renders nothing when the user may open fewer than two of the views', () => {
    auth.permitted = false;

    const { container } = render(
      <MemoryRouter initialEntries={['/sales/quotes']}>
        <ViewTabs primaryKey="quotes" />
      </MemoryRouter>,
    );

    // A single tab is a label pretending to be a control.
    expect(container).toBeEmptyDOMElement();
  });
});
