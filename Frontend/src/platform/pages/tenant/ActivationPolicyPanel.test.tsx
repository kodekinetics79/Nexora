import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type {
  ActivationControlDecision, Tenant, TenantActivationDecision, TenantBillingProfile,
  TenantOffboardingStatus,
} from '../../types';
import ActivationPolicyPanel from './ActivationPolicyPanel';
import LifecycleTab from './LifecycleTab';
import TenantDetailPage from '../TenantDetailPage';

/**
 * These behaviour tests used to live in DataStorageTab.test.tsx, then moved to Lifecycle with the
 * panel. Both placements were defensible from the inside and invisible from the outside: an
 * operator looking for "activate this tenant" does not open the tab whose other five buttons
 * delete the customer. Noor Sons sat in Provisioning for three days with all eight provisioning
 * steps recorded Succeeded for exactly that reason.
 *
 * The panel now has its own tab, second in the row. The tests render it DIRECTLY, because the
 * behaviour they assert is the panel's and not the tab's; the placement itself is pinned
 * separately at the bottom of this file, because "which screen is this on" is the property that
 * actually failed — twice.
 */

const permission = vi.hoisted(() => ({ isOwner: true, canAdministerBilling: true }));
vi.mock('../../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: permission.isOwner ? 'Owner' : 'SupportAdmin', isOwner: permission.isOwner,
    canAdministerTenants: true, canAdministerBilling: permission.canAdministerBilling,
    canImpersonate: permission.isOwner, roleUnknown: false,
  }),
}));

// `status` matters, but not as a freeze. The server refuses to LOOSEN a live tenant's profile and
// says "Tightening back to PRODUCTION is always available"; the panel now follows that rule rather
// than its old approximation of it.
const tenant = {
  id: '9', name: 'Acme', dataRegion: 'us-east-1', status: 'provisioning', baseCurrencyCode: 'USD',
} as Tenant;

const control = (over: Partial<ActivationControlDecision>): ActivationControlDecision => ({
  code: 'security.privileged-mfa-policy', satisfied: false,
  detail: 'Owner-approved privileged MFA evidence is required.', evidenceReferences: [],
  disposition: 'BLOCKING', blocksProduction: true, deferralKey: null, productionRequirement: null,
  remediation: null,
  ...over,
});

const activationBlocked: TenantActivationDecision = {
  tenantId: '9', ready: false, commercialState: 'PROSPECT', accessState: 'RESTRICTED',
  dataState: 'LIVE', legalHoldState: 'NONE', policyVersion: 'tenant-activation/2026-08-10.v2',
  evaluatedAtUtc: '2026-08-08T12:00:00Z', warnings: [],
  blockingControls: ['security.privileged-mfa-policy'],
  controls: [control({
    remediation: {
      surface: 'tenant.activation', action: 'tenant.activation-evidence',
      label: 'Record the MFA attestation', requiredAuthority: 'OwnerMfa',
      hint: 'The tenant identity plane persists no MFA assurance.',
    },
  })],
  // A PRODUCTION tenant: nothing is deferrable, so blocking and production-blocking agree.
  deploymentProfile: 'PRODUCTION',
  deploymentProfileDetail: 'PRODUCTION: every activation control is a hard gate and nothing is deferrable.',
  productionBlockingControls: ['security.privileged-mfa-policy'],
  deferredControls: [],
  externallyBlockedControls: [],
  certificationOnlyControls: [],
  productionReadiness: {
    certifiable: false,
    blockingControls: ['security.privileged-mfa-policy'],
    prerequisites: [],
    detail: 'Not certifiable.',
  },
};

const renderPanel = (subject: Tenant = tenant) => render(
  <MemoryRouter>
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SnackbarProvider><ActivationPolicyPanel tenant={subject} /></SnackbarProvider>
    </QueryClientProvider>
  </MemoryRouter>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  permission.isOwner = true;
  permission.canAdministerBilling = true;
  vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue(activationBlocked);
});

describe('ActivationPolicyPanel', () => {
  it('shows the full activation blockers and never enables a client-side override', async () => {
    renderPanel();
    expect(await screen.findByText('Authoritative tenant activation')).toBeVisible();
    expect(screen.getAllByText('security.privileged-mfa-policy')).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Activate tenant' })).toBeDisabled();
    expect(screen.getByText(/server transition changes tenant state/i)).toBeVisible();
  });

  it('requires real evidence metadata before recording an activation attestation', async () => {
    const recordEvidence = vi.spyOn(platformApi, 'recordTenantActivationEvidence').mockResolvedValue({
      tenantId: '9', controlCode: 'security.privileged-mfa-policy', disposition: 'approved',
      evidenceReference: 'https://evidence.example/mfa/9', effectiveFromUtc: '2026-08-08T12:00:00Z',
      effectiveToUtc: null, policyVersion: activationBlocked.policyVersion,
    });
    renderPanel();
    fireEvent.click(await screen.findByRole('button', { name: 'Record the MFA attestation' }));
    const evidenceDialog = within(await screen.findByRole('dialog', { name: 'Record activation control evidence' }));
    const submit = evidenceDialog.getByRole('button', { name: 'Record immutable evidence' });
    expect(submit).toBeDisabled();
    fireEvent.change(await evidenceDialog.findByLabelText(/Evidence URL/), { target: { value: 'not-a-url' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Evidence SHA-256/), { target: { value: 'bad' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Approval reason/), { target: { value: 'Independent policy review completed' } });
    expect(submit).toBeDisabled();

    fireEvent.change(evidenceDialog.getByLabelText(/Evidence URL/), { target: { value: 'https://evidence.example/mfa/9' } });
    fireEvent.change(evidenceDialog.getByLabelText(/Evidence SHA-256/), { target: { value: 'a'.repeat(64) } });
    fireEvent.click(submit);
    await waitFor(() => expect(recordEvidence).toHaveBeenCalledWith('9', 'security.privileged-mfa-policy',
      expect.objectContaining({ disposition: 'approved', evidenceSha256: 'a'.repeat(64) })));
  });

  /**
   * An attestation form is never pre-filled. The evidence URL, the content hash and the approval
   * reason are what an auditor reads as the platform's own word about a control nobody here can
   * check, so a form that suggested any of them would be manufacturing the evidence it exists to
   * record — and the operator who accepted the suggestion would never know they had.
   */
  it('opens every attestation field blank', async () => {
    renderPanel();
    fireEvent.click(await screen.findByRole('button', { name: 'Record the MFA attestation' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Record activation control evidence' }));
    expect(dialog.getByLabelText(/Evidence URL/)).toHaveValue('');
    expect(dialog.getByLabelText(/Evidence SHA-256/)).toHaveValue('');
    expect(dialog.getByLabelText(/Approval reason/)).toHaveValue('');
  });

  /**
   * The deployment profile had a server endpoint, a request DTO, a frontend type and a client
   * method — and no control. Everything existed except the thing an operator could click, so a
   * demo tenant whose externally-supplied prerequisites can never be satisfied had no reachable
   * path to activation. These tests exist because "the API supports it" was already true while the
   * feature was unusable.
   */
  it('offers the deployment profile control and refuses a reason the server would reject', async () => {
    const setProfile = vi.spyOn(platformApi, 'setTenantDeploymentProfile')
      .mockResolvedValue({ ...tenant, deploymentProfile: 'DEMO' } as Tenant);
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Change profile' }));
    const dialog = within(await screen.findByRole('dialog', { name: /Deployment profile for Acme/ }));
    const submit = dialog.getByRole('button', { name: 'Record profile' });

    // PRODUCTION is the current profile and needs no reason, so the button is live immediately.
    expect(submit).toBeEnabled();

    fireEvent.mouseDown(dialog.getByLabelText('Profile'));
    fireEvent.click(await screen.findByRole('option', { name: 'DEMO' }));

    // Off PRODUCTION a reason becomes mandatory, and anything under 15 characters is exactly what
    // the server answers 400 to — so the form must refuse it rather than discover it on submit.
    expect(submit).toBeDisabled();
    fireEvent.change(dialog.getByLabelText(/Reason/), { target: { value: 'too short' } });
    expect(submit).toBeDisabled();

    fireEvent.change(dialog.getByLabelText(/Reason/), { target: { value: 'Demonstration tenant with no customer data' } });
    expect(submit).toBeEnabled();
    fireEvent.click(submit);

    await waitFor(() => expect(setProfile).toHaveBeenCalledWith('9', {
      profile: 'DEMO', reason: 'Demonstration tenant with no customer data',
    }));
  });

  it('does not let a non-Owner change the deployment profile', async () => {
    permission.isOwner = false;
    renderPanel();
    expect(await screen.findByRole('button', { name: 'Change profile' })).toBeDisabled();
  });

  /**
   * The live-tenant recovery path.
   *
   * The button was `disabled={tenant.status !== 'provisioning'}`, which reads the server's rule as
   * "a live tenant's profile is frozen". It is not: SetDeploymentProfile refuses only to LOOSEN a
   * live tenant, and says "Tightening back to PRODUCTION is always available". The cost was real —
   * a tenant activated under LOCAL_TEST is permanently uncertifiable while it stays there, and the
   * one action that fixes it was the action the console greyed out, with nothing on screen saying
   * the server would have allowed it.
   */
  it('lets a live tenant tighten back to PRODUCTION', async () => {
    const setProfile = vi.spyOn(platformApi, 'setTenantDeploymentProfile')
      .mockResolvedValue({ ...tenant, deploymentProfile: 'PRODUCTION' } as Tenant);
    vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue({
      ...activationBlocked, deploymentProfile: 'LOCAL_TEST',
    });

    renderPanel({ ...tenant, status: 'active' } as Tenant);

    // Labelled for the only move available, not for the general one.
    const button = await screen.findByRole('button', { name: 'Tighten to PRODUCTION' });
    expect(button).toBeEnabled();
    fireEvent.click(button);

    // No reason field: the server requires one only to move OFF production, and demanding one to
    // tighten would be a hurdle in front of the safe direction.
    expect(screen.queryByLabelText(/Reason/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Record profile' }));

    // null, not '': tightening a gate needs no justification and the server enforces that
    // asymmetry, so the console sends nothing rather than an empty string it would have to parse.
    await waitFor(() => expect(setProfile).toHaveBeenCalledWith('9', {
      profile: 'PRODUCTION', reason: null,
    }));
  });

  it('offers no profile change to a live tenant already on PRODUCTION', async () => {
    // The strictest profile with nothing to tighten to. Disabled rather than hidden, and the
    // tooltip says a live tenant cannot be relabelled onto a looser one.
    renderPanel({ ...tenant, status: 'active' } as Tenant);
    expect(await screen.findByRole('button', { name: 'Tighten to PRODUCTION' })).toBeDisabled();
  });

  /**
   * The disposition chip.
   *
   * It rendered `satisfied ? 'Pass' : 'Block'`, which answers a different question from the one an
   * operator is asking. Under an approved DEMO profile the four deferrable controls are
   * unsatisfied and NOT blocking — the server says so — and every one of them still showed a red
   * "Block" indistinguishable from the controls that really do stop the activation. Fourteen red
   * rows, four of them noise, and nothing on screen saying which four.
   */
  it('renders the disposition the server returned, not just satisfied-or-not', async () => {
    vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue({
      ...activationBlocked,
      ready: true,
      deploymentProfile: 'DEMO',
      blockingControls: [],
      deferredControls: ['data.residency-isolation'],
      externallyBlockedControls: ['integrations.mandatory'],
      controls: [
        control({ code: 'commercial.plan', disposition: 'BLOCKING' }),
        control({
          code: 'data.residency-isolation', disposition: 'DEFERRED',
          productionRequirement: 'A versioned backup policy and a dated restore drill.',
        }),
        control({
          code: 'integrations.mandatory', disposition: 'EXTERNALLY_BLOCKED',
          productionRequirement: 'The customer ERP is connected.',
        }),
        control({ code: 'audit.health', satisfied: true, disposition: 'SATISFIED', blocksProduction: false }),
      ],
    });
    renderPanel();

    expect(await screen.findByText('Blocking')).toBeVisible();
    expect(screen.getByText('Deferred')).toBeVisible();
    expect(screen.getByText('Externally blocked')).toBeVisible();
    expect(screen.getByText('Pass')).toBeVisible();
    // The deferral is an activation on this profile and nothing else, so what production still
    // needs stays on screen beside it.
    expect(screen.getByText(/A versioned backup policy and a dated restore drill/)).toBeVisible();
  });

  /**
   * The map from a blocking control to the screen that owns its fix. The endpoints were all
   * already there and so were the screens; nothing anywhere said which of eleven tabs owned
   * "commercial.rate-card", which is what turned one tenant into eleven form submissions across
   * twelve surfaces.
   */
  it('offers a per-control remedy gated on the authority the server will apply', async () => {
    permission.canAdministerBilling = false;
    vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue({
      ...activationBlocked,
      blockingControls: ['commercial.rate-card', 'admin.first-activated'],
      controls: [
        control({
          code: 'commercial.rate-card',
          detail: 'A pinned, effective rate card is required.',
          remediation: {
            surface: 'tenant.commercial', action: 'tenant.rate-card-pin',
            label: 'Pin a rate card', requiredAuthority: 'Billing',
            hint: 'Only an active card in the tenant base currency satisfies the control.',
          },
        }),
        control({ code: 'admin.first-activated', detail: 'The founding administrator must have activated.' }),
      ],
    });
    renderPanel();

    // Billing authority the operator does not hold: disabled and explained, never hidden.
    expect(await screen.findByRole('button', { name: 'Pin a rate card' })).toBeDisabled();
    expect(screen.getByText(/Only an active card in the tenant base currency/)).toBeVisible();

    // A control with deliberately no resolver gets no button and says why — an operator who is
    // shown nothing assumes a missing feature and asks for it to be built.
    expect(screen.queryByRole('button', { name: /admin\.first-activated/ })).toBeNull();
    expect(screen.getByText(/No console action satisfies this control/)).toBeVisible();
  });

  /**
   * The remedy is the SAME call the owning tab makes. There is deliberately no "resolve activation
   * control" endpoint: a privileged one-shot that clears gates is an escape hatch, and an escape
   * hatch that exists is an escape hatch that gets used at 2am.
   */
  it('resolves a rate-card block through the ordinary billing endpoint', async () => {
    vi.spyOn(platformApi, 'getTenantActivationDecision').mockResolvedValue({
      ...activationBlocked,
      blockingControls: ['commercial.rate-card'],
      controls: [control({
        code: 'commercial.rate-card',
        detail: 'A pinned, effective rate card is required.',
        remediation: {
          surface: 'tenant.commercial', action: 'tenant.rate-card-pin',
          label: 'Pin a rate card', requiredAuthority: 'Billing',
          hint: 'Only an active card in the tenant base currency satisfies the control.',
        },
      })],
    });
    vi.spyOn(platformApi, 'listRateCards').mockResolvedValue([
      {
        id: '77', code: 'STANDARD-2026', currency: 'USD', effectiveFromUtc: '2026-01-01T00:00:00Z',
        effectiveToUtc: null, isActive: true, createdOn: '2026-01-01T00:00:00Z', createdBy: 'ops',
        version: 1,
        lines: [{ id: '1', meterKey: 'docs.processed', includedQuantity: 0, unitPrice: 1, unit: 'doc', tierNote: null }],
      },
      // Ineligible: inactive, so pinning it would leave the control red with nothing on screen
      // explaining which of the four rules it broke.
      {
        id: '78', code: 'LEGACY-2024', currency: 'USD', effectiveFromUtc: '2024-01-01T00:00:00Z',
        effectiveToUtc: null, isActive: false, createdOn: '2024-01-01T00:00:00Z', createdBy: 'ops',
        version: 1, lines: [],
      },
    ]);
    const setRateCard = vi.spyOn(platformApi, 'setTenantRateCard')
      .mockResolvedValue({ tenantId: '9' } as TenantBillingProfile);
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Pin a rate card' }));
    const dialog = within(await screen.findByRole('dialog'));
    const confirm = dialog.getByRole('button', { name: 'Pin this card' });
    expect(confirm).toBeDisabled();

    fireEvent.mouseDown(await dialog.findByLabelText(/Rate card/));
    // The inactive card must not even be offered.
    expect(screen.queryByRole('option', { name: /LEGACY-2024/ })).toBeNull();
    fireEvent.click(await screen.findByRole('option', { name: /STANDARD-2026/ }));

    // A reason is mandatory and starts blank, exactly as it does on the Commercial tab.
    expect(confirm).toBeDisabled();
    fireEvent.change(dialog.getByLabelText(/Reason/), { target: { value: 'Negotiated standard pricing for pilot.' } });
    fireEvent.click(confirm);

    await waitFor(() => expect(setRateCard).toHaveBeenCalledWith('9', {
      rateCardId: '77', reason: 'Negotiated standard pricing for pilot.',
    }));
  });

  /**
   * The placement pin.
   *
   * Activation is the first thing an operator does to a tenant and this is the only screen naming
   * the blocking controls or carrying the Activate button, so if it is not somewhere obvious an
   * operator cannot find it — which is the failure this test exists to catch, and which has now
   * happened twice. It renders the real page rather than asserting on an import, because an unused
   * import type-checks and still renders nothing.
   *
   * It also pins the ORDER. "Second, after Overview" is the property; being present somewhere in
   * an eleven-tab scroller is what it looked like the last two times.
   */
  it('is rendered on its own tab, second in the row, and no longer on Lifecycle', async () => {
    vi.spyOn(platformApi, 'getTenant').mockResolvedValue(tenant);
    vi.spyOn(platformApi, 'getOffboarding').mockRejectedValue(new Error('not needed for this assertion'));

    render(
      <MemoryRouter initialEntries={['/platform/tenants/9?tab=activation']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SnackbarProvider>
            <Routes><Route path="/platform/tenants/:id" element={<TenantDetailPage />} /></Routes>
          </SnackbarProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    const tabs = await screen.findAllByRole('tab');
    expect(tabs[0]).toHaveTextContent('Overview');
    expect(tabs[1]).toHaveTextContent('Activation');

    expect(await screen.findByText('Authoritative tenant activation')).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Activate tenant' })).toBeInTheDocument();
  });

  /**
   * The other half of the pin. Leaving the panel on Lifecycle as well would put two Activate
   * buttons on two tabs — two ways to fire the same one-way transition, and the second one is
   * always the one nobody tested.
   */
  it('is no longer rendered on the Lifecycle tab', async () => {
    // A complete Live status. The arrays matter: LifecycleTab reads history/exports/disclosures
    // eagerly, so a partial stub crashes the render and the test would pass for a reason that has
    // nothing to do with where the panel is mounted.
    vi.spyOn(platformApi, 'getOffboarding').mockResolvedValue({
      tenantId: '9', tenantName: 'Acme', tenantSlug: 'acme', tenantStatus: 'Provisioning',
      stage: 'NotScheduled',
      retentionDays: null, deletionScheduledOn: null, purgeEligibleOn: null, isPurgeEligible: false,
      daysUntilPurgeEligible: null, deletionReason: null, deletionScheduledBy: null,
      purgedOn: null, purgedBy: null, purgedRowCount: null,
      personalDataErasedOn: null, personalDataErasedBy: null, erasedIdentityCount: null,
      lastExportedOn: null, lastExportedBy: null,
      canScheduleDeletion: false, canCancelDeletion: false, canPurge: false,
      canErasePersonalData: false, purgeRequiresDifferentApprover: false,
      deletionApprovedBy: null, confirmationRequired: 'Acme',
      history: [], exports: [], disclosures: [],
    } as unknown as TenantOffboardingStatus);
    vi.spyOn(platformApi, 'listTenantLegalHolds').mockResolvedValue([]);

    render(
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SnackbarProvider><LifecycleTab tenant={tenant} /></SnackbarProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText('Where this tenant stands')).toBeVisible();
    expect(screen.queryByText('Authoritative tenant activation')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Activate tenant' })).toBeNull();
  });
});
