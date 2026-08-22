import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

/**
 * Seven rail entries address a filtered list through a query string — the four quote states,
 * "Ready for Quote", "Sourcing Cases" and lead "Revisions". `isPathMatched` compared
 * `location.pathname`, which NEVER carries a query, so not one of them could ever highlight; group
 * selection is `children.some(...)`, so the parent row went dark with them.
 *
 * The cost was not a cosmetic one. A rep clicked "Quote Management > Sent Quotes" at 4pm to chase
 * offers, landed on three rows with nothing lit in a 69-row rail and no filter stated anywhere,
 * and reported to their manager that the pipeline was nearly empty. The rail is one of the two
 * places that has to say which slice of the data is on screen.
 */

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

// These tests pin how the rail MATCHES an address that carries a query string. That behaviour is
// independent of which entries the pilot rail happens to show, and three of the entries it needs
// ("Follow-up Due", "Ready for Quote", "Duplicates") are hidden from the pilot. Exercise the full
// rail so the matcher is tested on every shape it must handle, not only the pilot subset.
//
// Hoisted because PILOT_RAIL_ENABLED is a module-level constant, read once when Sidebar is
// imported — stubbing after the import would be too late.
vi.hoisted(() => { vi.stubEnv('VITE_PILOT_RAIL', 'off'); });

import Sidebar from './Sidebar';

function renderRail(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Sidebar collapsed={false} />
    </MemoryRouter>,
  );
}

const row = (name: string | RegExp) => screen.getByRole('button', { name });

describe('a rail entry whose address carries a filter', () => {
  it('highlights on the matching URL', () => {
    renderRail('/sales/quotes?state=sent');

    expect(row('Sent Quotes')).toHaveAttribute('aria-current', 'page');
  });

  it('lights only the state the rep actually opened', () => {
    renderRail('/sales/quotes?state=sent');

    expect(row('Draft Quotes')).not.toHaveAttribute('aria-current');
    expect(row('Follow-up Due')).not.toHaveAttribute('aria-current');
    expect(row('Won / Lost')).not.toHaveAttribute('aria-current');
  });

  it('carries the highlight up to the group row', () => {
    renderRail('/sales/quotes?state=follow-up');

    // Groups expose expand state rather than aria-current, so selection is the MUI class.
    expect(row(/^Quote Management/).className).toContain('Mui-selected');
  });

  it('stands the unfiltered sibling down when a filter is on the address', () => {
    // "All RFQs" and "Ready for Quote" are the same pathname. A lit "All RFQs" over a narrowed
    // grid is the same lie the constant page heading was.
    renderRail('/procurement/rfqs/all?state=ready-for-quote');

    expect(row('Ready for Quote')).toHaveAttribute('aria-current', 'page');
    expect(row('all_rfqs')).not.toHaveAttribute('aria-current');
  });

  it('leaves the unfiltered entry lit for a param that is not a filter', () => {
    renderRail('/procurement/rfqs/all?page=2');

    expect(row('all_rfqs')).toHaveAttribute('aria-current', 'page');
  });

  it('still highlights an entry that carries no query at all', () => {
    // CONTROL: this passed before the fix too. It is here so a regression in the plain path
    // comparison — the case that always worked — cannot hide behind the new query handling.
    renderRail('/procurement/leads/duplicates');

    expect(row('Duplicates')).toHaveAttribute('aria-current', 'page');
  });
});
