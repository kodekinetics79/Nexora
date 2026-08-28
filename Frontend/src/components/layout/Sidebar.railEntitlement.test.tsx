import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/** Operational workspaces stay discoverable regardless of legacy navigation entitlements. */

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

import Sidebar from './Sidebar';

const renderRail = () =>
  render(
    <MemoryRouter initialEntries={['/inbox']}>
      <Sidebar collapsed={false} />
    </MemoryRouter>,
  );

const CollapsedRailHarness = () => {
  const [collapsed, setCollapsed] = useState(true);

  return (
    <>
      <output aria-label="Rail state">{collapsed ? 'collapsed' : 'expanded'}</output>
      <Sidebar collapsed={collapsed} onRequestExpand={() => setCollapsed(false)} />
    </>
  );
};

describe('the rail keeps operational workspaces discoverable', () => {
  beforeEach(() => {
    auth.entitlements = [];
  });

  it('shows grouped workspaces when the legacy entitlement is not granted', () => {
    renderRail();

    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Catalogue & stock' })).toBeInTheDocument();
    expect(screen.getByText('More workspaces')).toBeInTheDocument();
  });

  it('does not change the information architecture for a legacy entitlement', () => {
    auth.entitlements = ['capability.full-navigation'];

    renderRail();

    expect(screen.getByText('More workspaces')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Catalogue & stock' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
  });

  it('an unrelated entitlement does not hide workspaces', () => {
    auth.entitlements = ['capability.exports'];

    renderRail();

    expect(screen.getByRole('button', { name: 'Inbox' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Catalogue & stock' })).toBeInTheDocument();
  });

  it('keeps the searchable directory as a fallback', () => {
    renderRail();
    expect(screen.getByRole('button', { name: 'Screen directory' })).toBeInTheDocument();
  });

  it('opens the requested workspace when its collapsed icon is activated', () => {
    render(
      <MemoryRouter initialEntries={['/inbox']}>
        <CollapsedRailHarness />
      </MemoryRouter>,
    );

    const catalogue = screen.getByRole('button', { name: 'Catalogue & stock' });
    expect(screen.getByRole('status', { name: 'Rail state' })).toHaveTextContent('collapsed');
    expect(screen.queryByRole('button', { name: 'Products' })).not.toBeInTheDocument();

    fireEvent.click(catalogue);

    expect(screen.getByRole('status', { name: 'Rail state' })).toHaveTextContent('expanded');
    expect(screen.getByRole('button', { name: 'Catalogue & stock' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Products' })).toBeInTheDocument();
  });

  it('renders only list items as direct children of every list', () => {
    const { container } = renderRail();
    fireEvent.click(screen.getByRole('button', { name: 'Catalogue & stock' }));

    for (const list of container.querySelectorAll('ul')) {
      expect(Array.from(list.children).every((child) => child.tagName === 'LI')).toBe(true);
    }
  });
});
