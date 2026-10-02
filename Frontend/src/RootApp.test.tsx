import { render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

const authState = { token: null as string | null };

vi.mock('./context/AuthContext', () => ({
  useAuth: () => ({ token: authState.token }),
}));

vi.mock('./components/layout/RouteAnnouncer', () => ({ default: () => null }));
vi.mock('./App', () => ({ default: () => <div>authenticated workspace</div> }));
vi.mock('./pages/Login/LoginPage', () => ({ default: () => <div>public login</div> }));
vi.mock('./pages/Activation/ActivateAccountPage', () => ({ default: () => <div>public activation</div> }));
vi.mock('./pages/PasswordReset/ForgotPasswordPage', () => ({ default: () => <div>public forgot password</div> }));
vi.mock('./pages/PasswordReset/ResetPasswordPage', () => ({ default: () => <div>public reset password</div> }));

import RootApp from './RootApp';

const LocationProbe = () => {
  const location = useLocation();
  return <output aria-label="current route">{location.pathname}</output>;
};

const renderAt = (entry: string) => render(
  <MemoryRouter initialEntries={[entry]}>
    <LocationProbe />
    <RootApp />
  </MemoryRouter>,
);

describe('public/authenticated bundle boundary', () => {
  it.each([
    ['/login', 'public login'],
    ['/activate/invitation-token', 'public activation'],
    ['/forgot-password', 'public forgot password'],
    ['/reset-password/reset-token', 'public reset password'],
  ])('renders %s without mounting the authenticated workspace', async (path, pageText) => {
    authState.token = null;
    renderAt(path);

    expect(await screen.findByText(pageText)).toBeInTheDocument();
    expect(screen.queryByText('authenticated workspace')).not.toBeInTheDocument();
  });

  it('loads the workspace router for tenant paths', async () => {
    authState.token = 'signed-in';
    renderAt('/inbox');

    expect(await screen.findByText('authenticated workspace')).toBeInTheDocument();
  });

  it('redirects an anonymous root visit into the public login graph', async () => {
    authState.token = null;
    renderAt('/');

    expect(await screen.findByText('public login')).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'current route' })).toHaveTextContent('/login');
  });

  it('redirects an authenticated root visit into the workspace graph', async () => {
    authState.token = 'signed-in';
    renderAt('/');

    expect(await screen.findByText('authenticated workspace')).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'current route' })).toHaveTextContent('/dashboard');
  });
});
