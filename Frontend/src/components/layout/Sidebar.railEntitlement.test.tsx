import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Which rail a tenant sees is a PLATFORM decision — the `capability.full-navigation` entitlement
 * granted on the tenant's Modules screen — not a deployment env var. These tests pin the three
 * states the client can be in: granted (full rail), not granted (pilot rail), and the fail-closed
 * default when the bootstrap has not answered or an older server never reports entitlements.
 *
 * "Follow-up Due" is the canary row: it exists in the full quote group and is absent from the
 * pilot allow-list, so its presence tells the two rails apart with a single query. "Draft Quotes"
 * is the control — on both rails, so a regression that empties the rail entirely cannot pass as
 * "trimmed".
 */

const auth = {
  entitlements: [] as string[],
};

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: () => true,
    hasEntitlement: (key: string) => auth.entitlements.includes(key),
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

import Sidebar, { FULL_NAVIGATION_ENTITLEMENT } from './Sidebar';

// A URL inside Quote Management, so the group renders expanded: rows only exist in the DOM once
// their group is open, and an assertion against a closed group would pass for the wrong reason.
const renderRail = () =>
  render(
    <MemoryRouter initialEntries={['/sales/quotes?state=sent']}>
      <Sidebar collapsed={false} />
    </MemoryRouter>,
  );

describe('the rail obeys the tenant full-navigation entitlement', () => {
  beforeEach(() => {
    auth.entitlements = [];
  });

  it('shows the pilot rail when the entitlement is not granted', () => {
    renderRail();

    expect(screen.getAllByRole('button', { name: 'Sent Quotes' }).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Follow-up Due' })).toBeNull();
  });

  it('shows the full rail when the platform grants the entitlement', () => {
    auth.entitlements = [FULL_NAVIGATION_ENTITLEMENT];

    renderRail();

    expect(screen.getAllByRole('button', { name: 'Follow-up Due' }).length).toBeGreaterThan(0);
  });

  it('an unrelated entitlement grants nothing', () => {
    // The rail must ask for its OWN key, not for "any entitlement exists".
    auth.entitlements = ['capability.exports'];

    renderRail();

    expect(screen.getAllByRole('button', { name: 'Sent Quotes' }).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Follow-up Due' })).toBeNull();
  });
});
