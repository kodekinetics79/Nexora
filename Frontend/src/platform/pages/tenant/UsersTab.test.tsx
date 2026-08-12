import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantRole, TenantUser } from '../../types';
import UsersTab from './UsersTab';

vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

const tenant = { id: '3', name: 'Acme', slug: 'acme', status: 'active' } as Tenant;

const founder: TenantUser = {
  id: '12', firstName: 'Dana', middleName: null, lastName: 'Okafor',
  email: 'founder@acme.test', roleId: '90', roleCode: 'SUPER_ADMIN',
  roleName: 'Super Administrator', roleRank: 30, isActive: true, deactivatedAtUtc: null,
  lastLogin: '2026-08-09T09:00:00Z', createdOn: '2026-08-01T09:00:00Z',
  invitation: null, awaitingActivation: false,
};

const invitee: TenantUser = {
  id: '13', firstName: 'Layla', middleName: null, lastName: 'Haddad',
  email: 'layla@acme.test', roleId: '92', roleCode: 'SALES_REP',
  roleName: 'Sales Representative', roleRank: 0, isActive: false, deactivatedAtUtc: null,
  lastLogin: null, createdOn: '2026-08-09T09:00:00Z',
  invitation: {
    id: '5', userId: '13', email: 'layla@acme.test', status: 'Pending',
    issuedAtUtc: '2026-08-09T09:00:00Z', expiresAtUtc: '2026-08-12T09:00:00Z',
    redeemedAtUtc: null, revokedAtUtc: null, revokedBy: null, revocationReason: null,
    lastSentAtUtc: '2026-08-09T09:00:01Z', sendCount: 1, issuedBy: 'operator@nexora.test',
  },
  awaitingActivation: true,
};

const roles: TenantRole[] = [
  {
    id: '90', code: 'SUPER_ADMIN', name: 'Super Administrator', description: null,
    rank: 30, rankLabel: 'Owner', activeUserCount: 1, grantable: true, notGrantableReason: null,
  },
  {
    id: '91', code: 'SALES_MANAGER', name: 'Sales Manager', description: null,
    rank: 10, rankLabel: 'Manager', activeUserCount: 0, grantable: true, notGrantableReason: null,
  },
  {
    id: '92', code: 'SALES_REP', name: 'Sales Representative', description: null,
    rank: 0, rankLabel: 'Member', activeUserCount: 0, grantable: true, notGrantableReason: null,
  },
];

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><UsersTab tenant={tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'listTenantUsers').mockResolvedValue([founder, invitee]);
  vi.spyOn(platformApi, 'listTenantRoles').mockResolvedValue(roles);
});

describe('UsersTab', () => {
  it('shows every account with its role, rank and invitation state', async () => {
    renderTab();

    expect(await screen.findByText('Dana Okafor')).toBeVisible();
    expect(screen.getByText('layla@acme.test')).toBeVisible();
    // The rank is surfaced beside the label because the label carries no authority.
    expect(screen.getByText('Owner rank')).toBeVisible();
    expect(screen.getByText('Awaiting activation')).toBeVisible();
    expect(screen.getByText('Invite: Pending')).toBeVisible();
  });

  it('creates a user against the chosen role and states why', async () => {
    const create = vi.spyOn(platformApi, 'createTenantUser').mockResolvedValue({
      user: { ...invitee, id: '14', email: 'new@acme.test' },
      invitation: invitee.invitation,
      emailDispatched: true,
      activationUrl: null,
    });
    renderTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Add user' }));
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: 'new@acme.test' } });
    fireEvent.change(screen.getByLabelText(/First name/), { target: { value: 'Omar' } });
    fireEvent.change(screen.getByLabelText(/Last name/), { target: { value: 'Nasser' } });
    fireEvent.mouseDown(screen.getByLabelText(/Role/));
    fireEvent.click(await screen.findByRole('option', { name: /Sales Manager/ }));
    fireEvent.change(screen.getByLabelText(/Reason/), {
      target: { value: 'Buyer starting before the founding administrator signs in.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create and invite' }));

    await waitFor(() => expect(create).toHaveBeenCalledWith('3', expect.objectContaining({
      email: 'new@acme.test', firstName: 'Omar', lastName: 'Nasser', roleId: '91',
      activation: 'invite',
      reason: 'Buyer starting before the founding administrator signs in.',
    })));
  });

  it('shows the one-time link when the provider did not transmit the invitation', async () => {
    vi.spyOn(platformApi, 'createTenantUser').mockResolvedValue({
      user: { ...invitee, id: '14', email: 'new@acme.test' },
      invitation: invitee.invitation,
      emailDispatched: false,
      activationUrl: 'https://app.nexora.test/activate/single-use-token',
    });
    renderTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Add user' }));
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: 'new@acme.test' } });
    fireEvent.change(screen.getByLabelText(/First name/), { target: { value: 'Omar' } });
    fireEvent.change(screen.getByLabelText(/Last name/), { target: { value: 'Nasser' } });
    fireEvent.mouseDown(screen.getByLabelText(/Role/));
    fireEvent.click(await screen.findByRole('option', { name: /Sales Representative/ }));
    fireEvent.change(screen.getByLabelText(/Reason/), {
      target: { value: 'Mail relay at the customer is refusing our sender.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create and invite' }));

    expect(await screen.findByText('https://app.nexora.test/activate/single-use-token')).toBeVisible();
    expect(screen.getByText(/copy this link now/i)).toBeVisible();
  });

  it('deactivates with a reason and says the activation link dies with it', async () => {
    const deactivate = vi.spyOn(platformApi, 'deactivateTenantUser')
      .mockResolvedValue({ ...founder, isActive: false, deactivatedAtUtc: '2026-08-10T10:00:00Z' });
    renderTab();

    const row = (await screen.findByText('founder@acme.test')).closest('tr')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Deactivate' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/withdrawn in the same transaction/)).toBeVisible();
    fireEvent.change(within(dialog).getByLabelText(/Reason/), {
      target: { value: 'Left the customer and their access was revoked by request.' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

    await waitFor(() => expect(deactivate).toHaveBeenCalledWith(
      '3', '12', 'Left the customer and their access was revoked by request.'));
  });

  it('refuses a role change to the role the person already holds', async () => {
    const change = vi.spyOn(platformApi, 'changeTenantUserRole').mockResolvedValue(founder);
    renderTab();

    const row = (await screen.findByText('founder@acme.test')).closest('tr')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Change role' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/Reason/), {
      target: { value: 'Customer asked for the change on ticket 4471.' },
    });
    expect(within(dialog).getByText('That is the role they already hold.')).toBeVisible();
    expect(within(dialog).getByRole('button', { name: 'Change role' })).toBeDisabled();

    fireEvent.mouseDown(within(dialog).getByLabelText(/New role/));
    fireEvent.click(await screen.findByRole('option', { name: /Sales Manager/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Change role' }));

    await waitFor(() => expect(change).toHaveBeenCalledWith('3', '12', {
      roleId: '91', reason: 'Customer asked for the change on ticket 4471.',
    }));
  });

  it('offers a reissue only for somebody who has never redeemed a link', async () => {
    const resend = vi.spyOn(platformApi, 'resendTenantAdminInvitation').mockResolvedValue({
      invitation: invitee.invitation!, emailDispatched: true, activationUrl: null,
    });
    renderTab();

    const founderRow = (await screen.findByText('founder@acme.test')).closest('tr')!;
    expect(within(founderRow).queryByRole('button', { name: 'Resend invite' })).toBeNull();

    const inviteeRow = screen.getByText('layla@acme.test').closest('tr')!;
    fireEvent.click(within(inviteeRow).getByRole('button', { name: 'Resend invite' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/Reason/), {
      target: { value: 'The first email landed in a shared mailbox nobody reads.' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reissue & send' }));

    await waitFor(() => expect(resend).toHaveBeenCalledWith('3', {
      userId: '13', reason: 'The first email landed in a shared mailbox nobody reads.',
    }));
  });
});
