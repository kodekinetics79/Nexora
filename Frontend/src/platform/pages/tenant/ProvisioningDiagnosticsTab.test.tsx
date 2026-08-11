import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantProvisioningDiagnostics } from '../../types';
import ProvisioningDiagnosticsTab from './ProvisioningDiagnosticsTab';

/**
 * The screen that replaced "Provisioning failed."
 *
 * Each test below asserts one thing the old single sentence could not say: what actually broke,
 * whose problem it is, what is missing, whether pressing the button helps, and which of the
 * outstanding blockers matter to a laptop as opposed to a real customer.
 */

const tenant = {
  id: '9',
  name: 'Noorline Trading',
  deploymentProfile: 'PRODUCTION',
  deploymentProfileReason: null,
  deploymentProfileApprovedBy: null,
  deploymentProfileApprovedOn: null,
} as Tenant;

const diagnostics = (
  overrides: Partial<TenantProvisioningDiagnostics> = {},
): TenantProvisioningDiagnostics => ({
  tenantId: '9',
  tenantName: 'Noorline Trading',
  tenantStatus: 'Provisioning',
  deploymentProfile: 'PRODUCTION',
  executionId: '41',
  status: 'Failed',
  currentStep: null,
  steps: [
    {
      step: 'tenant', label: 'Create tenant record', ordinal: 0, status: 'Succeeded',
      attemptCount: 1, startedOn: null, completedOn: null, durationMs: 12,
      failureCode: null, failureReason: null,
    },
    {
      step: 'founding-admin', label: 'Create founding administrator', ordinal: 5, status: 'Failed',
      attemptCount: 2, startedOn: null, completedOn: null, durationMs: 30,
      failureCode: 'email-taken',
      failureReason: "A user with email 'ada@noorline.test' already exists on another tenant.",
    },
  ],
  completedSteps: ['tenant'],
  failedStep: {
    step: 'founding-admin', label: 'Create founding administrator', ordinal: 5, status: 'Failed',
    attemptCount: 2, startedOn: null, completedOn: null, durationMs: 30,
    failureCode: 'email-taken',
    failureReason: "A user with email 'ada@noorline.test' already exists on another tenant.",
  },
  failureReason: "A user with email 'ada@noorline.test' already exists on another tenant.",
  failureCode: 'email-taken',
  missingPrerequisite: 'A founding-administrator email address that is not already in use.',
  classification: 'CUSTOMER_INPUT',
  classificationDetail:
    'The founding administrator email address is already held by another user. User email is globally unique, so no retry can free it.',
  correlationId: 'prov-7f3c9a11',
  attemptCount: 2,
  completedStepCount: 1,
  totalStepCount: 8,
  startedOn: '2026-08-10T09:00:00Z',
  completedOn: '2026-08-10T09:00:05Z',
  recoveryActions: [
    {
      action: 'resume', step: null, available: true, safe: false,
      detail: 'Accepted, but it cannot succeed as submitted: the runner judged this failure terminal.',
    },
  ],
  productionBlockers: [
    {
      code: 'integrations.mandatory', scope: 'PRODUCTION', disposition: 'EXTERNALLY_BLOCKED',
      detail: 'Mandatory integration health requires current evidence.',
      productionRequirement: "The customer's ERP or accounting system is connected.",
    },
    {
      code: 'identity.legal-customer', scope: 'PRODUCTION', disposition: 'BLOCKING',
      detail: 'Legal name, registration number, country and customer contact must be recorded.',
      productionRequirement: null,
    },
  ],
  localTestBlockers: [
    {
      code: 'identity.legal-customer', scope: 'LOCAL_TEST', disposition: 'BLOCKING',
      detail: 'Legal name, registration number, country and customer contact must be recorded.',
      productionRequirement: null,
    },
  ],
  blockersUnavailableReason: null,
  evaluatedAtUtc: '2026-08-10T09:05:00Z',
  ...overrides,
});

const renderTab = (subject: Tenant = tenant) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><ProvisioningDiagnosticsTab tenant={subject} /></SnackbarProvider>
  </QueryClientProvider>,
);

// Owner, so nothing under test is disabled for want of authority except where a test says so.
vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner', isOwner: true, canAdministerTenants: true,
    canAdministerBilling: true, canImpersonate: true, roleUnknown: false,
  }),
}));

beforeEach(() => {
  vi.restoreAllMocks();
});

describe('provisioning diagnostics', () => {
  it('names the failed step, the reason, the classification and the missing prerequisite', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(diagnostics());

    renderTab();

    await waitFor(() =>
      expect(screen.getByText(/Create founding administrator failed/i)).toBeInTheDocument());

    // The four things the old single sentence could not say.
    expect(screen.getAllByText(/already exists on another tenant/i).length).toBeGreaterThan(0);
    expect(screen.getByText('Customer input')).toBeInTheDocument();
    expect(screen.getByText(/Missing prerequisite/i)).toBeInTheDocument();
    expect(screen.getByText(/not already in use/i)).toBeInTheDocument();

    // The technical handle an engineer needs, and the machine-readable code.
    expect(screen.getByText(/Correlation prov-7f3c9a11/)).toBeInTheDocument();
    expect(screen.getByText(/Failure code email-taken/)).toBeInTheDocument();

    // It is never only "Provisioning failed."
    expect(screen.queryByText(/^Provisioning failed\.$/)).not.toBeInTheDocument();
  });

  it('separates production blockers from local-test blockers', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(diagnostics());

    renderTab();

    const production = (await screen.findByText('Production blockers')).closest('div');
    const localTest = (await screen.findByText('Local-test blockers')).closest('div');
    expect(production).not.toBeNull();
    expect(localTest).not.toBeNull();

    // The customer's ERP is owed to production and is irrelevant to a laptop, so it appears in
    // exactly one of the two lists.
    expect(within(production!).getByText('integrations.mandatory')).toBeInTheDocument();
    expect(within(localTest!).queryByText('integrations.mandatory')).not.toBeInTheDocument();

    // A defect in the tenant's own record is owed to both.
    expect(within(production!).getByText('identity.legal-customer')).toBeInTheDocument();
    expect(within(localTest!).getByText('identity.legal-customer')).toBeInTheDocument();

    // The deferral vocabulary reaches the screen rather than being flattened to "blocked".
    expect(within(production!).getByText('EXTERNALLY_BLOCKED')).toBeInTheDocument();
  });

  it('explains an unsafe recovery instead of leaving the control unexplained', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(diagnostics());

    renderTab();

    // The label appears twice on purpose — once as the heading of the explanation, once on the
    // control it explains — so this asserts on the control itself.
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Resume provisioning' })).toBeInTheDocument());

    // Offered, because the server would accept it — and the reason it will not help is on the
    // surface, not only in a tooltip.
    expect(screen.getByText(/the runner judged this failure terminal/i)).toBeInTheDocument();
    expect(screen.getByText(/not expected to help/i)).toBeInTheDocument();
  });

  it('states why a queued tenant never moved, with nothing failed', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(diagnostics({
      status: 'Pending',
      failedStep: null,
      failureCode: null,
      failureReason: 'This request was accepted and journalled but no runner has ever picked it up.',
      classification: 'PLATFORM_CONFIGURATION',
      classificationDetail: 'The request was accepted and journalled, but no runner has claimed it.',
      missingPrerequisite: 'A running ProvisioningRunWorker. It is DISABLED in this deployment.',
      recoveryActions: [{
        action: 'resume', step: null, available: true, safe: true,
        detail: 'The runner picks up at the first step that has not succeeded.',
      }],
    }));

    renderTab();

    // Named twice: the chip in the header and the title of the explanation beneath it.
    await waitFor(() =>
      expect(screen.getAllByText('Platform configuration').length).toBeGreaterThan(0));
    expect(screen.getByText(/no runner has ever picked it up/i)).toBeInTheDocument();
    expect(screen.getByText(/A running ProvisioningRunWorker/i)).toBeInTheDocument();
    // The customer did nothing, and the screen says so rather than implying a bad request.
    expect(screen.getByText(/a platform engineer has to act/i)).toBeInTheDocument();
  });

  it('reports an unapproved DEMO profile as deferring nothing', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(
      diagnostics({ deploymentProfile: 'DEMO' }),
    );

    renderTab({ ...tenant, deploymentProfile: 'DEMO' } as Tenant);

    await waitFor(() =>
      expect(screen.getByText('Deployment profile: DEMO')).toBeInTheDocument());
    expect(screen.getByText(/No Owner approval is recorded/i)).toBeInTheDocument();
    expect(screen.getByText(/fails closed exactly\s+as PRODUCTION/i)).toBeInTheDocument();
  });

  it('says why the blocker lists are empty when they are unknown rather than clean', async () => {
    vi.spyOn(platformApi, 'getTenantProvisioningDiagnostics').mockResolvedValue(diagnostics({
      productionBlockers: [],
      localTestBlockers: [],
      blockersUnavailableReason:
        'No tenant record exists yet, so the activation controls cannot be evaluated.',
    }));

    renderTab();

    await waitFor(() =>
      expect(screen.getByText('Blockers are unknown, not empty')).toBeInTheDocument());
    expect(screen.getByText(/No tenant record exists yet/i)).toBeInTheDocument();
  });
});
