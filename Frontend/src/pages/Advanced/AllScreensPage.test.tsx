import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const auth = vi.hoisted(() => ({ grants: new Set<string>() }));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: (moduleName: string) => auth.grants.has(moduleName),
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
  beforeEach(() => auth.grants.clear());

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

  it('keeps Setup in the administrative directory instead of the commercial shortcuts', () => {
    auth.grants.add('Users');
    renderPage();

    const shortcuts = within(screen.getByRole('navigation', { name: 'Primary navigation shortcuts' }));
    expect(shortcuts.queryByRole('link', { name: 'Setup' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Setup/ })).toBeInTheDocument();
  });
});
