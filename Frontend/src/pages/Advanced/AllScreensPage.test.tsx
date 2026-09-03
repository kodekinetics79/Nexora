import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const auth = vi.hoisted(() => ({ grants: new Set<string>(), entitlements: new Set<string>() }));

// `hasEntitlement` is part of the auth contract (AuthContext.tsx:108) and the directory now hides
// whole groups behind it, so the stub has to answer it. Absence is fail-closed there, and it is
// here too: a test that wants an entitled group adds the key.
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: (moduleName: string) => auth.grants.has(moduleName),
    hasEntitlement: (key: string) => auth.entitlements.has(key),
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

import AllScreensPage from './AllScreensPage';

const renderPage = () => render(
  <MemoryRouter>
    <AllScreensPage />
  </MemoryRouter>,
);

describe('Screen directory primary navigation', () => {
  beforeEach(() => {
    auth.grants.clear();
    auth.entitlements.clear();
  });

  it('does not advertise primary destinations the role cannot open', () => {
    auth.grants.add('Leads');
    renderPage();

    const shortcuts = within(screen.getByRole('navigation', { name: 'Primary navigation shortcuts' }));
    expect(shortcuts.getByRole('link', { name: 'Inbox' })).toBeInTheDocument();
    expect(shortcuts.getByRole('link', { name: 'Leads' })).toBeInTheDocument();
    expect(shortcuts.queryByRole('link', { name: 'RFQs' })).not.toBeInTheDocument();
    expect(shortcuts.queryByRole('link', { name: 'Quotes' })).not.toBeInTheDocument();
    expect(shortcuts.queryByRole('link', { name: 'Setup' })).not.toBeInTheDocument();
  });

  // The rail is not the only door to Copilot: this page lists every screen by name, so a tenant
  // without the AI capability must not find it here either. Sidebar.toolsEntitlement covers the rail.
  it('hides the AI group from the directory until the tenant holds capability.ai', () => {
    auth.grants.add('Dashboard');
    const { unmount } = renderPage();
    expect(screen.queryByRole('heading', { name: 'Assistants & tools' })).not.toBeInTheDocument();
    expect(screen.queryAllByRole('link', { name: /^Copilot/ })).toHaveLength(0);
    unmount();

    auth.entitlements.add('capability.ai');
    renderPage();
    expect(screen.getByRole('heading', { name: 'Assistants & tools' })).toBeInTheDocument();
    expect(screen.queryAllByRole('link', { name: /^Copilot/ }).length).toBeGreaterThan(0);
  });

  it('keeps Setup in the administrative directory instead of the commercial shortcuts', () => {
    auth.grants.add('Users');
    renderPage();

    const shortcuts = within(screen.getByRole('navigation', { name: 'Primary navigation shortcuts' }));
    expect(shortcuts.queryByRole('link', { name: 'Setup' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Setup/ })).toBeInTheDocument();
  });
});
