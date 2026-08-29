import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RequireManager } from './PermissionGuard';

const auth = vi.hoisted(() => ({
  token: 'test-token' as string | null,
  isManager: false,
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    token: auth.token,
    userData: { isManager: auth.isManager },
  }),
}));

const renderGuard = () => render(
  <MemoryRouter>
    <RequireManager><div>Team controls</div></RequireManager>
  </MemoryRouter>,
);

describe('RequireManager', () => {
  beforeEach(() => {
    auth.token = 'test-token';
    auth.isManager = false;
  });

  it('fails closed for a signed-in member even when a route has a module read grant', () => {
    renderGuard();

    expect(screen.getByRole('heading', { name: /manager access required/i })).toBeInTheDocument();
    expect(screen.queryByText('Team controls')).not.toBeInTheDocument();
  });

  it('renders manager, admin, and owner-ranked users represented by server isManager', () => {
    auth.isManager = true;
    renderGuard();

    expect(screen.getByText('Team controls')).toBeInTheDocument();
  });
});
