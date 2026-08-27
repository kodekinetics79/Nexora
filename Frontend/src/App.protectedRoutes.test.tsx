import { render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const authState = { token: null as string | null };

vi.mock('./context/AuthContext', () => ({
  useAuth: () => ({
    token: authState.token,
    userData: {},
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: () => {},
  }),
}));

vi.mock('./components/layout/MainLayout', () => ({
  default: ({ children }: { children: React.ReactNode }) => <div data-testid="tenant-shell">{children}</div>,
}));

vi.mock('./components/layout/RouteAnnouncer', () => ({ default: () => null }));
vi.mock('./pages/Login/LoginPage', () => ({ default: () => <div>tenant login</div> }));
vi.mock('./pages/Inbox/InboxPage', () => ({ default: () => <div>tenant inbox</div> }));
vi.mock('./pages/Advanced/AllScreensPage', () => ({ default: () => <div>advanced directory</div> }));

import App from './App';

const LocationProbe = () => {
  const location = useLocation();
  return <output aria-label="current route">{location.pathname}</output>;
};

const renderAt = (entry: string) => render(
  <MemoryRouter initialEntries={[entry]}>
    <LocationProbe />
    <App />
  </MemoryRouter>,
);

beforeEach(() => {
  authState.token = null;
});

describe('authenticated tenant-shell routes', () => {
  it.each(['/inbox', '/advanced', '/home', '/today'])(
    'redirects an anonymous visitor from %s to login without rendering the tenant shell',
    async (entry) => {
      renderAt(entry);

      expect(await screen.findByText('tenant login')).toBeInTheDocument();
      expect(screen.getByRole('status', { name: 'current route' })).toHaveTextContent('/login');
      expect(screen.queryByTestId('tenant-shell')).not.toBeInTheDocument();
    },
  );

  it('still renders the module-agnostic Inbox for an authenticated user', async () => {
    authState.token = 'signed-in';

    renderAt('/inbox');

    expect(await screen.findByText('tenant inbox')).toBeInTheDocument();
    expect(screen.getByTestId('tenant-shell')).toBeInTheDocument();
  });
});
