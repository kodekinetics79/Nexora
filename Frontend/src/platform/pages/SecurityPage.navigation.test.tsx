import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const platform = vi.hoisted(() => ({
  role: 'SupportAdmin',
  listImpersonationSessions: vi.fn(),
  listAudit: vi.fn(),
}));

vi.mock('../api/client', () => ({
  platformApi: {
    listImpersonationSessions: platform.listImpersonationSessions,
    listAudit: platform.listAudit,
    revokeImpersonation: vi.fn(),
  },
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

vi.mock('../components/PlatformMfaPanel', () => ({ default: () => null }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));

import SecurityPage from './SecurityPage';

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <SecurityPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

describe('Security page authentication-policy navigation', () => {
  beforeEach(() => {
    platform.role = 'SupportAdmin';
    platform.listImpersonationSessions.mockReset().mockResolvedValue([]);
    platform.listAudit.mockReset().mockResolvedValue([]);
  });

  it('explains the Owner boundary without offering a link that ends in 403', () => {
    renderPage();

    expect(screen.getByRole('button', { name: 'Open Platform Authentication' })).toBeDisabled();
    expect(screen.queryByRole('link', { name: 'Open Platform Authentication' })).not.toBeInTheDocument();
  });

  it('links Owners to the policy screen', () => {
    platform.role = 'Owner';
    renderPage();

    expect(screen.getByRole('link', { name: 'Open Platform Authentication' }))
      .toHaveAttribute('href', '/platform/security/authentication');
  });
});
