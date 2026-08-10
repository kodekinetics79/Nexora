import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantAdminInvitation } from '../../types';
import ProfileAccessTab from './ProfileAccessTab';

const tenant = {
  id: '3', name: 'Acme', slug: 'acme', status: 'provisioning', legalName: 'Acme LLC',
  countryCode: 'US', industry: null, registrationNumber: null, taxNumber: null, website: null,
  addressLine1: null, addressLine2: null, city: null, stateProvince: null, postalCode: null,
  phone: null, contactEmail: 'admin@acme.test', logoUrl: null, timeZoneId: 'America/New_York',
  locale: 'en-US',
} as Tenant;

const invitation: TenantAdminInvitation = {
  id: '1', userId: '12', email: 'admin@acme.test', status: 'Pending',
  issuedAtUtc: '2026-08-09T20:02:54Z', expiresAtUtc: '2026-08-10T20:02:54Z',
  redeemedAtUtc: null, revokedAtUtc: null, revokedBy: null, revocationReason: null,
  lastSentAtUtc: null, sendCount: 0, issuedBy: 'owner@nexora.app',
};

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><ProfileAccessTab tenant={tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'listTenantAdminInvitations').mockResolvedValue([invitation]);
});

describe('ProfileAccessTab', () => {
  it('saves an audited tenant profile update', async () => {
    const update = vi.spyOn(platformApi, 'updateTenantProfile').mockResolvedValue({
      ...tenant, name: 'Acme Aerospace', industry: 'Aerospace',
    });
    renderTab();

    fireEvent.change(screen.getByLabelText(/Trading name/), { target: { value: 'Acme Aerospace' } });
    fireEvent.change(screen.getByLabelText(/Industry/), { target: { value: 'Aerospace' } });
    fireEvent.change(screen.getByLabelText(/Reason for change/), { target: { value: 'Customer legal profile correction' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save audited changes' }));

    await waitFor(() => expect(update).toHaveBeenCalledWith('3', expect.objectContaining({
      name: 'Acme Aerospace', industry: 'Aerospace', reason: 'Customer legal profile correction',
    })));
  });

  it('shows the one-time activation link when the provider did not transmit email', async () => {
    vi.spyOn(platformApi, 'resendTenantAdminInvitation').mockResolvedValue({
      invitation, emailDispatched: false, activationUrl: 'https://nexora1-ai.vercel.app/activate/single-use-token',
    });
    renderTab();

    expect(await screen.findByText('Never transmitted by an email provider')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Reissue & send' }));

    expect(await screen.findByText('https://nexora1-ai.vercel.app/activate/single-use-token')).toBeVisible();
    expect(screen.getByText(/copy this link now/i)).toBeVisible();
  });
});
