import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

// The guard and layout pull in the platform session, axios and MUI chrome. None of that is
// under test here — the question is purely which path the router settles on — so both are
// reduced to pass-throughs and the pages to markers.
vi.mock('./components/PlatformGuard', () => ({
  default: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));
vi.mock('./components/PlatformLayout', async () => {
  // vi.mock factories are hoisted above the import block, so the module cannot be closed over
  // from the top of the file. importActual is the vitest-native way to reach it — require() is
  // CommonJS and this project typechecks as ESM without @types/node, so it does not compile.
  const { Outlet } = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { default: () => <Outlet /> };
});
vi.mock('./pages/OverviewPage', () => ({ default: () => <div>overview page</div> }));
vi.mock('./pages/TenantsPage', () => ({ default: () => <div>tenants page</div> }));
vi.mock('./pages/TenantDetailPage', () => ({ default: () => <div>tenant detail page</div> }));
vi.mock('./pages/PipelinePage', () => ({ default: () => <div>pipeline page</div> }));
vi.mock('./pages/PlansFlagsPage', () => ({ default: () => <div>plans page</div> }));
vi.mock('./pages/PlatformUsersPage', () => ({ default: () => <div>users page</div> }));
vi.mock('./pages/BillingPage', () => ({ default: () => <div>billing page</div> }));
vi.mock('./pages/SupportPage', () => ({ default: () => <div>support page</div> }));
vi.mock('./pages/SecurityPage', () => ({ default: () => <div>security page</div> }));
vi.mock('./pages/AuditLogPage', () => ({ default: () => <div>audit page</div> }));

import PlatformRoutes from './PlatformRoutes';

/** Renders the platform tree at `entry` and reports where the router ends up. */
function renderAt(entry: string) {
  let path = entry;
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route
          path="/platform/*"
          element={
            <>
              <PlatformRoutes />
              <LocationProbe onChange={(next) => { path = next; }} />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
  return () => path;
}

function LocationProbe({ onChange }: { onChange: (path: string) => void }) {
  const location = useLocation();
  onChange(location.pathname);
  return null;
}

describe('platform console routing', () => {
  it('lands an unknown platform path on the console home instead of looping', async () => {
    // Regression: the catch-all used a RELATIVE redirect, so /platform/login resolved to
    // /platform/login/overview, then /platform/login/overview/overview, and so on until the
    // router threw and the error boundary replaced the whole console with "Something went
    // wrong". The redirect must be absolute.
    const currentPath = renderAt('/platform/anything-unmatched');

    expect(await screen.findByText('overview page')).toBeInTheDocument();
    await waitFor(() => expect(currentPath()).toBe('/platform/overview'));
    expect(currentPath()).not.toContain('overview/overview');
  });

  it('accepts /platform/login, the address people actually type', async () => {
    const currentPath = renderAt('/platform/login');

    expect(await screen.findByText('overview page')).toBeInTheDocument();
    await waitFor(() => expect(currentPath()).toBe('/platform/overview'));
  });

  it('sends the tree root to the console home', async () => {
    const currentPath = renderAt('/platform');

    expect(await screen.findByText('overview page')).toBeInTheDocument();
    await waitFor(() => expect(currentPath()).toBe('/platform/overview'));
  });

  it('still serves a real page without redirecting it', async () => {
    const currentPath = renderAt('/platform/billing');

    expect(await screen.findByText('billing page')).toBeInTheDocument();
    expect(currentPath()).toBe('/platform/billing');
  });

  it('serves the support desk rather than swallowing it into the catch-all', async () => {
    const currentPath = renderAt('/platform/support');

    expect(await screen.findByText('support page')).toBeInTheDocument();
    expect(currentPath()).toBe('/platform/support');
  });
});
