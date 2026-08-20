import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

/**
 * `/setup` shipped with its auth gate on the CHILD routes only. The index route — the hub — had no
 * guard, so an anonymous visitor got the full authenticated shell over copy reading "No setup
 * screens are open to your role. Ask an administrator to grant the modules you need." A signed-out
 * person was told their ROLE was the problem, and sent to an administrator who would find nothing
 * wrong. It was the only route of ~120 under MainLayout without a guard.
 *
 * The gate now sits on the shell, so the hub and every child — including any added later — inherit
 * it. These tests pin the redirect for the hub specifically, because the hub is the one that was
 * broken and the one no child-route guard would ever have covered.
 */

const authState = { token: null as string | null };

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    token: authState.token,
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: () => {},
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

import SetupShell from './SetupShell';

/** Renders the setup tree at `entry` and reports where the router settles. */
function renderAt(entry: string) {
  let path = entry;
  const Probe = () => {
    path = useLocation().pathname;
    return null;
  };
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Probe />
      <Routes>
        <Route path="/setup" element={<SetupShell />}>
          <Route index element={<div>setup hub</div>} />
          <Route path="currency" element={<div>currency screen</div>} />
        </Route>
        <Route path="/login" element={<div>login screen</div>} />
      </Routes>
    </MemoryRouter>,
  );
  return () => path;
}

describe('SetupShell auth gate', () => {
  it('sends an anonymous visitor at the /setup hub to the login screen', () => {
    authState.token = null;
    const at = renderAt('/setup');

    expect(at()).toBe('/login');
    expect(screen.getByText('login screen')).toBeInTheDocument();
    // The regression: the shell and hub must not render at all without a token.
    expect(screen.queryByText('setup hub')).not.toBeInTheDocument();
  });

  it('sends an anonymous visitor at a setup child screen to the login screen', () => {
    authState.token = null;
    const at = renderAt('/setup/currency');

    expect(at()).toBe('/login');
    expect(screen.queryByText('currency screen')).not.toBeInTheDocument();
  });

  it('renders the hub for a signed-in user', () => {
    authState.token = 'a-real-token';
    renderAt('/setup');

    expect(screen.getByText('setup hub')).toBeInTheDocument();
    expect(screen.queryByText('login screen')).not.toBeInTheDocument();
  });

  it('renders a child screen for a signed-in user', () => {
    authState.token = 'a-real-token';
    renderAt('/setup/currency');

    expect(screen.getByText('currency screen')).toBeInTheDocument();
  });
});
