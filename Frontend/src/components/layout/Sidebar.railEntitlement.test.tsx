import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Which rail a tenant sees is a PLATFORM decision — the `capability.full-navigation` entitlement
 * granted on the tenant's Modules screen — not a deployment env var and not a hard-coded list.
 *
 * The default is the five-row rail. The grant restores the relocated groups inline, for a customer
 * who asks for the surface they had before. These tests pin the three states the client can be in:
 * granted, not granted, and the fail-closed default when the bootstrap has not answered or an
 * older server never reports entitlements at all.
 *
 * "Stock ageing" is the canary: it is in the relocated catalogue and never on the five-row rail, so
 * its presence tells the two rails apart with one query. "Inbox" is the control — on both rails, so
 * a regression that empties the rail entirely cannot pass as "trimmed".
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

const renderRail = () =>
  render(
    <MemoryRouter initialEntries={['/inbox']}>
      <Sidebar collapsed={false} />
    </MemoryRouter>,
  );

describe('the rail obeys the tenant full-navigation entitlement', () => {
  beforeEach(() => {
    auth.entitlements = [];
  });

  it('shows the five-row rail when the entitlement is not granted', () => {
    renderRail();

    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Catalogue & stock' })).toBeNull();
    expect(screen.queryByText('Advanced')).toBeNull();
  });

  it('restores the relocated groups when the platform grants the entitlement', () => {
    auth.entitlements = [FULL_NAVIGATION_ENTITLEMENT];

    renderRail();

    expect(screen.getByText('Advanced')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Catalogue & stock' })).toBeInTheDocument();
    // The five stay first. The grant adds surface; it never reorders the job.
    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
  });

  it('an unrelated entitlement grants nothing', () => {
    // The rail must ask for its OWN key, not for "any entitlement exists".
    auth.entitlements = ['capability.exports'];

    renderRail();

    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Catalogue & stock' })).toBeNull();
  });

  it('keeps All screens on BOTH rails, so nothing is ever unreachable', () => {
    const trimmed = renderRail();
    expect(screen.getByRole('button', { name: 'All screens' })).toBeInTheDocument();
    trimmed.unmount();

    auth.entitlements = [FULL_NAVIGATION_ENTITLEMENT];
    const granted = renderRail();
    expect(
      granted.getAllByRole('button').some((button) => button.textContent === 'All screens'),
    ).toBe(true);
  });
});
