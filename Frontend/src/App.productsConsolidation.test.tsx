import type { ReactNode } from 'react';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const authState = { token: 'signed-in' as string | null, canViewProducts: true };

vi.mock('./context/AuthContext', () => ({
  useAuth: () => ({
    token: authState.token,
    userData: {},
    hasPermission: (moduleName: string) => moduleName !== 'Products' || authState.canViewProducts,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
  }),
}));
vi.mock('./components/layout/MainLayout', () => ({
  default: ({ children }: { children: ReactNode }) => <main>{children}</main>,
}));
vi.mock('./components/layout/RouteAnnouncer', () => ({ default: () => null }));
vi.mock('./pages/Inventory/ProductsPage', () => ({ default: () => <h1>Unified products list</h1> }));
vi.mock('./pages/Login/LoginPage', () => ({ default: () => <h1>Tenant login</h1> }));

import App from './App';

function LocationProbe() {
  const { pathname, search, hash } = useLocation();
  return <output aria-label="current route">{`${pathname}${search}${hash}`}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <LocationProbe />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  authState.token = 'signed-in';
  authState.canViewProducts = true;
});

describe('consolidated Products bookmarks', () => {
  it.each(['/inventory/pricing-sheet', '/inventory/availability'])(
    'redirects %s to Products without dropping product context', async (path) => {
      renderAt(`${path}?productId=42&search=valve#details`);
      expect(await screen.findByRole('heading', { name: 'Unified products list' })).toBeInTheDocument();
      expect(screen.getByLabelText('current route')).toHaveTextContent('/inventory/products?productId=42&search=valve#details');
    },
  );

  it.each(['/inventory/pricing-sheet', '/inventory/availability'])(
    'keeps the Products permission gate on %s', async (path) => {
      authState.canViewProducts = false;
      renderAt(path);
      expect(await screen.findByRole('heading', { name: 'Access Denied' })).toBeInTheDocument();
      expect(screen.queryByRole('heading', { name: 'Unified products list' })).not.toBeInTheDocument();
    },
  );

  it.each(['/inventory/pricing-sheet', '/inventory/availability'])(
    'requires sign-in for %s', async (path) => {
      authState.token = null;
      renderAt(path);
      expect(await screen.findByRole('heading', { name: 'Tenant login' })).toBeInTheDocument();
      expect(screen.queryByRole('heading', { name: 'Unified products list' })).not.toBeInTheDocument();
    },
  );
});
