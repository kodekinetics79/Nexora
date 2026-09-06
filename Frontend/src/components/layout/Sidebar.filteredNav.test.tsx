import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * What the rail shows, and where it says you are.
 *
 * The rail is a seven-stage commercial spine. The test that used to live here pinned a much harder problem: seven rail
 * entries addressed a FILTERED list through a query string ("Sent Quotes", "Ready for Quote",
 * "Sourcing Cases", lead "Revisions") and `location.pathname` never carries a query, so none of
 * them could ever highlight. The cost was not cosmetic — a rep clicked "Quote Management > Sent
 * Quotes" at 4pm to chase offers, landed on three rows with nothing lit anywhere in a 69-row rail
 * and no filter stated on the page, and told their manager the pipeline was nearly empty.
 *
 * Those seven entries are now TABS on the screens they filter, so the highlight problem moved with
 * them (see `ViewTabs.test.tsx`, which pins the same matching rules). What the rail must still get
 * right is the case below: a filtered address is still INSIDE its primary destination, so the row
 * stays lit rather than going dark while the tab strip says which slice is on screen.
 */

const auth = vi.hoisted(() => ({ grants: null as Set<string> | null }));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: (moduleName: string) => auth.grants === null || auth.grants.has(moduleName),
    hasEntitlement: () => false,
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

import Sidebar from './Sidebar';

function renderRail(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Sidebar collapsed={false} />
    </MemoryRouter>,
  );
}

const row = (name: string | RegExp) => screen.getByRole('button', { name });

describe('the rail presents the commercial spine and grouped workspaces', () => {
  beforeEach(() => {
    auth.grants = null;
  });

  it('shows the primary destinations, grouped workspaces and searchable directory', () => {
    renderRail('/inbox');

    expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual(expect.arrayContaining([
      'Inbox',
      'Leads',
      'RFQs',
      'Quotes',
      'Orders',
      'Fulfilment',
      'Receivables',
      'Administration',
      'Customers & ownership',
      'Suppliers & sourcing',
      'Customer PO & handoffs',
      'Catalogue & stock',
      'Screen directory',
    ]));
  });

  it('shows only the post-quote stages granted to the current role', () => {
    auth.grants = new Set(['Orders']);
    renderRail('/sales/orders');

    expect(row('Orders')).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByRole('button', { name: 'Fulfilment' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Receivables' })).not.toBeInTheDocument();
  });

  it('keeps secondary workspaces collapsed until needed', () => {
    renderRail('/inbox');

    expect(screen.getAllByRole('button', { expanded: false }).length).toBeGreaterThan(0);
  });

  it('automatically opens the workspace containing the current screen', () => {
    renderRail('/inventory/ageing');

    expect(row('Catalogue & stock')).toHaveAttribute('aria-expanded', 'true');
    expect(row('Stock ageing')).toHaveAttribute('aria-current', 'page');
  });
});

describe('where the rail says you are', () => {
  beforeEach(() => {
    auth.grants = null;
  });

  it('lights the row you are on', () => {
    renderRail('/inbox');

    expect(row('Inbox')).toHaveAttribute('aria-current', 'page');
  });

  it('stays lit on a FILTERED address inside the destination', () => {
    // The old rail went dark here, because the filter lived in a query string on a child row.
    renderRail('/sales/quotes?state=sent');

    expect(row('Quotes')).toHaveAttribute('aria-current', 'page');
  });

  it('lights only one row at a time', () => {
    renderRail('/sales/quotes?state=sent');

    expect(row('Leads')).not.toHaveAttribute('aria-current');
    expect(row('RFQs')).not.toHaveAttribute('aria-current');
    expect(row('Orders')).not.toHaveAttribute('aria-current');
    expect(row('Fulfilment')).not.toHaveAttribute('aria-current');
    expect(row('Receivables')).not.toHaveAttribute('aria-current');
  });

  it('lights the owning row on a DETAIL page, not just on the list', () => {
    // A rep reading one quote has not left Quotes. The old rail lit nothing here.
    renderRail('/sales/quotes/view/42');

    expect(row('Quotes')).toHaveAttribute('aria-current', 'page');
  });

  it('lights Inbox on each of the intake screens it owns', () => {
    // These were four separate rail rows under "Lead Management"; they are views of Inbox now, and
    // the rail has to agree with the tab strip about that.
    for (const url of [
      '/procurement/extraction/review',
      '/procurement/leads/inbound-mail',
      '/procurement/leads/manual-upload',
      '/procurement/leads/intelligence',
      '/procurement/leads/ingestion/123',
    ]) {
      const view = renderRail(url);
      expect(screen.getByRole('button', { name: 'Inbox' })).toHaveAttribute('aria-current', 'page');
      expect(screen.getByRole('button', { name: 'Leads' })).not.toHaveAttribute('aria-current');
      expect(document.querySelectorAll('[aria-current="page"]')).toHaveLength(1);
      view.unmount();
    }
  });

  it('lights RFQs on the legacy /rfqs alias, which is still a live route', () => {
    renderRail('/rfqs/view/7');

    expect(row('RFQs')).toHaveAttribute('aria-current', 'page');
  });

  it.each([
    ['/sales/orders/42', 'Orders'],
    ['/sales/shipments/21', 'Fulfilment'],
    ['/sales/finance', 'Receivables'],
  ])('keeps the post-quote journey visible at %s', (url, destination) => {
    renderRail(url);
    expect(row(destination)).toHaveAttribute('aria-current', 'page');
  });

  it('marks the owning workspace rather than the Screen directory on an operational screen', () => {
    renderRail('/analytics/deadlines');
    expect(row('Dashboards & analytics')).toHaveAttribute('aria-expanded', 'true');
    expect(row('Deadline board')).toHaveAttribute('aria-current', 'page');
    expect(row('Screen directory')).not.toHaveAttribute('aria-current');
  });

  /**
   * `/dashboard` is the Executive view now, and it is a manager-tier rail row rather than a card in
   * the directory. This harness signs in as a rep (`isManager: false`), so the address belongs to
   * no destination they have — and the rail says so by lighting nothing, which is the same answer
   * it gives for any address outside the reader's spine. A rep who still has the Dashboard module
   * permission can reach the screen by URL; what they no longer get is a navigation entry to a
   * screen whose panels would tell them the figures are for managers.
   */
  it('lights nothing for a rep on the manager-tier executive address', () => {
    renderRail('/dashboard');
    expect(screen.queryByRole('button', { name: 'Executive view' })).toBeNull();
    expect(document.querySelectorAll('[aria-current="page"]')).toHaveLength(0);
  });

  it('nests Setup under Administration without losing its current-screen signal', () => {
    renderRail('/setup');

    expect(row('Administration')).toHaveAttribute('aria-expanded', 'true');
    expect(row('Setup')).toHaveAttribute('aria-current', 'page');
  });

  it('lights nothing when the address belongs to no primary destination', () => {
    renderRail('/inventory/ageing');

    for (const name of ['Inbox', 'Leads', 'RFQs', 'Quotes', 'Orders', 'Fulfilment', 'Receivables']) {
      expect(row(name)).not.toHaveAttribute('aria-current');
    }
  });
});
