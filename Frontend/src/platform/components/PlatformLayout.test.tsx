import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const platform = vi.hoisted(() => ({ role: 'SupportAdmin' }));

vi.mock('../../context/ThemeContext', () => ({
  useAppTheme: () => ({ mode: 'light', setMode: vi.fn() }),
}));

vi.mock('../auth/usePlatformAuth', () => ({
  usePlatformAuth: () => ({
    platformUser: { name: 'Test Operator', email: 'operator@nexora.test', role: platform.role },
    platformLogout: vi.fn(),
  }),
}));

vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: platform.role,
    isOwner: platform.role === 'Owner',
    canAdministerTenants: platform.role === 'Owner' || platform.role === 'SupportAdmin',
    canAdministerBilling: platform.role === 'Owner' || platform.role === 'BillingAdmin',
    canImpersonate: platform.role === 'Owner' || platform.role === 'SupportAdmin',
    roleUnknown: false,
  }),
}));

vi.mock('./PlatformMfaEnforcementBanner', () => ({ default: () => null }));

import PlatformLayout from './PlatformLayout';

const renderLayout = () => render(
  <MemoryRouter initialEntries={['/platform/overview']}>
    <PlatformLayout />
  </MemoryRouter>,
);

describe('Platform console role-aware navigation', () => {
  beforeEach(() => { platform.role = 'SupportAdmin'; });

  it('does not send a non-Owner to the Owner-only authentication-policy read', () => {
    renderLayout();

    expect(screen.getByRole('link', { name: 'Security' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Platform Authentication' })).not.toBeInTheDocument();
  });

  it('keeps the authentication-policy destination discoverable for Owners', () => {
    platform.role = 'Owner';
    renderLayout();

    expect(screen.getByRole('link', { name: 'Platform Authentication' })).toBeInTheDocument();
  });
});
