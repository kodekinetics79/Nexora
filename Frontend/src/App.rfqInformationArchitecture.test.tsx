import type { ReactNode } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

vi.mock('./context/AuthContext', () => ({
  useAuth: () => ({
    token: 'signed-in',
    userData: {},
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
  }),
}));

vi.mock('./components/layout/MainLayout', () => ({
  default: ({ children }: { children: ReactNode }) => <main data-testid="tenant-shell">{children}</main>,
}));

vi.mock('./components/layout/RouteAnnouncer', () => ({ default: () => null }));
vi.mock('./pages/Leads/AssignedLeadsPage', () => ({
  default: () => <h1>Assigned Leads queue</h1>,
}));

import App from './App';

const LocationProbe = () => {
  const location = useLocation();
  return <output data-testid="current-location">{`${location.pathname}${location.search}`}</output>;
};

describe('legacy Outstanding RFQ bookmarks', () => {
  it('replace the old address with the canonical assigned-Lead queue', async () => {
    render(
      <MemoryRouter initialEntries={['/procurement/rfqs/outstanding']}>
        <App />
        <LocationProbe />
      </MemoryRouter>,
    );

    expect(await screen.findByRole('heading', { name: 'Assigned Leads queue' })).toBeInTheDocument();
    expect(screen.getByTestId('tenant-shell')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('current-location')).toHaveTextContent('/procurement/leads/assigned');
    });
  });
});
