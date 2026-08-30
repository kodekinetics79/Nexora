import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../api/client';
import type { ProvisioningExecution } from '../types';
import ProvisioningProgressDialog from './ProvisioningProgressDialog';

vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

/**
 * This dialog is the last thing an operator reads before deciding the job is done, and for the
 * first real tenant it told them so falsely. It said:
 *
 *   "Workspace ready — Every step committed. {name} is active and {adminEmail} can sign in."
 *
 * Both clauses were false. The runner sets `Status = Provisioning` explicitly, and a tenant in
 * Provisioning is denied access by design (`ITenantAccessService.IsAccessDenied` lists it first).
 * On the DEFAULT invite path the founding admin is written `IsActive = false` and stays that way
 * until the invitee redeems.
 *
 * The cost was not the wording. An operator told the workspace is ready has no reason to go
 * looking for an activation screen, so the outstanding controls went unseen for three days on a
 * tab nobody had opened. Nothing asserted this copy before — the claim was never tested, which is
 * how it survived.
 */

const execution = (overrides: Partial<ProvisioningExecution> = {}): ProvisioningExecution => ({
  id: 'exec-1',
  state: 'Succeeded',
  slug: 'acme',
  name: 'Acme Trading',
  adminEmail: 'admin@acme.example',
  adminActivation: 'invite',
  currentStep: null,
  failedStep: null,
  failureReason: null,
  failureIsTerminal: false,
  tenantId: '9',
  provisionedBusinessUnitId: '7',
  foundingUserId: '12',
  correlationId: 'corr-1',
  requestedBy: 'owner@nexora.app',
  createdOn: '2026-08-12T00:00:00Z',
  startedOn: '2026-08-12T00:00:01Z',
  completedOn: '2026-08-12T00:00:20Z',
  attemptCount: 1,
  cancelledBy: null,
  cancellationReason: null,
  steps: [],
  completedStepCount: 8,
  totalStepCount: 8,
  ...overrides,
});

const renderDialog = (submission?: { execution: ProvisioningExecution; created: boolean; generatedPassword?: string }) =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SnackbarProvider>
        <ProvisioningProgressDialog
          executionId="exec-1"
          submission={submission as never}
          onClose={() => {}}
        />
      </SnackbarProvider>
    </QueryClientProvider>,
  );

beforeEach(() => vi.restoreAllMocks());

describe('ProvisioningProgressDialog completion banner', () => {
  it('does not claim the tenant is active or that the admin can sign in', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution());
    renderDialog();

    expect(await screen.findByText(/Provisioned — not yet active/i)).toBeVisible();
    // The two false claims, by their exact shape.
    expect(screen.queryByText(/Workspace ready/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/is active and/i)).not.toBeInTheDocument();
  });

  it('names the state the tenant is actually in, and where to go next', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution());
    renderDialog();

    const banner = await screen.findByText(/Provisioned — not yet active/i);
    // Scope to the banner: "Provisioning" also appears in the dialog's own chrome, and asserting
    // globally would pass on the wrong element.
    const alert = banner.closest('.MuiAlert-root') as HTMLElement;
    expect(alert).toHaveTextContent(/Provisioning/);
    expect(alert).toHaveTextContent(/denies tenant access by design/i);
    // Without a next step the honest banner is just a dead end with better manners.
    expect(alert).toHaveTextContent(/Activation/);
  });

  it('tells the invite path that redemption is itself a blocking control', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution({ adminActivation: 'invite' }));
    renderDialog();

    await screen.findByText(/Provisioned — not yet active/i);
    expect(screen.getByText(/must redeem the link/i)).toBeVisible();
  });

  it('tells the password path the account is blocked by the tenant, not by the credential', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution({ adminActivation: 'password' }));
    renderDialog({ execution: execution({ adminActivation: 'password' }), created: true, generatedPassword: 'x'.repeat(24) });

    await screen.findByText(/Provisioned — not yet active/i);
    expect(screen.getByText(/cannot sign in until the tenant is active/i)).toBeVisible();
  });

  it('recognises an operator-supplied password without a generated secret', async () => {
    const suppliedPassword = execution({ adminActivation: 'password' });
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(suppliedPassword);
    renderDialog({ execution: suppliedPassword, created: true });

    await screen.findByText(/Provisioned — not yet active/i);
    expect(screen.getByText(/has a password but cannot sign in until the tenant is active/i)).toBeVisible();
    expect(screen.queryByText(/has been invited|must redeem the link/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/One-time password for/i)).not.toBeInTheDocument();
  });

  it('keeps the correct password instructions when an execution is reopened', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution({ adminActivation: 'password' }));
    renderDialog();

    await screen.findByText(/Provisioned — not yet active/i);
    expect(screen.getByText(/has a password but cannot sign in until the tenant is active/i)).toBeVisible();
    expect(screen.queryByText(/has been invited|must redeem the link/i)).not.toBeInTheDocument();
  });

  it('does not invent an invitation for an unknown activation method', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution({ adminActivation: 'unknown' }));
    renderDialog();

    await screen.findByText(/Provisioned — not yet active/i);
    expect(screen.queryByText(/has been invited|has a password|must redeem the link/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Check the administrator.*credential status/i)).toBeVisible();
  });

  it('uses the server step count instead of a hard-coded eight steps', async () => {
    vi.spyOn(platformApi, 'getProvisioningExecution').mockResolvedValue(execution({ totalStepCount: 9, completedStepCount: 9 }));
    renderDialog();

    expect(await screen.findByText(/9 steps, committed one at a time/i)).toBeVisible();
    expect(screen.queryByText(/Eight steps/i)).not.toBeInTheDocument();
  });
});
