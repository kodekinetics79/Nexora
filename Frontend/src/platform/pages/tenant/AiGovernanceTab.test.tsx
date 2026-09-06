import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type {
  AiExtractionReadinessCheck, AiExtractionReadinessReport, Tenant, TenantAiPolicy,
} from '../../types';
import AiGovernanceTab from './AiGovernanceTab';

/**
 * The panel that replaced "resubmit and find out".
 *
 * Five controls in three layers, each discoverable only by opening the previous one and sending
 * another document, is what dead-lettered every RFQ the 2026-08 pilot submitted. The tests below
 * assert the things a single denial code could never say: which controls are closed, all of them
 * at once, what to set each one to, where, and — for the one comparison that is case-sensitive —
 * a value the operator copies rather than retypes.
 */

const policy: TenantAiPolicy = {
  businessUnitId: '4', isEnabled: true, externalProcessingAllowed: false,
  allowedPurposes: ['RfqExtraction'], allowedProvider: null, allowedModel: null,
  monthlySoftTokenLimit: 10_000, monthlyHardTokenLimit: 20_000, maxTokensPerDocument: 2_000,
  externalInputCostPerMillionTokens: null, externalOutputCostPerMillionTokens: null,
  externalCostCurrency: null, externalPricingVersion: null, externalDependencyCeilingPercent: 5,
  redactionRequired: true, allowedDataClassifications: 'Public,Internal',
  egressPolicy: 'RedactedFieldsOnly', dataResidency: 'US', retentionDays: 30,
  inputOutputAuditAllowed: false, privacyReviewRequired: true, localComputeCostPerHour: null,
  ocrCostPerPage: null, localCostCurrency: null, version: 2,
  updatedOn: '2026-08-08T12:00:00Z', updatedBy: 'owner@nexora.local',
  planCode: 'internal-pilot-qa', planAiPackage: 'Private',
  planAiPackageName: 'Private extraction',
  planAiPackageMeans: 'AI processing on, external processing OFF, whole-document egress shut.',
  planAiMonthlyTokenAllowance: 2_000_000, planAiAllowanceUnlimited: false,
  planDeviations: [], planDeviationReason: null, planDeviationApprovedBy: null,
  planDeviationApprovedOn: null,
};

const resolvedProvider = {
  provider: 'Ollama', endpoint: 'https://ollama.example', model: 'deepseek-v4-pro',
  providerClass: 'External', classificationReason: 'non_loopback_endpoint', isResolved: true,
};

const check = (
  order: number,
  code: string,
  title: string,
  overrides: Partial<AiExtractionReadinessCheck> = {},
): AiExtractionReadinessCheck => ({
  order,
  code,
  title,
  status: 'Pass',
  denialReason: null,
  currentValue: 'satisfied',
  requiredValue: '',
  setItIn: '',
  detail: `Why ${code} exists.`,
  ...overrides,
});

const readiness = (
  overrides: Partial<AiExtractionReadinessReport> = {},
): AiExtractionReadinessReport => ({
  resolvedProvider,
  purpose: 'RfqExtraction',
  unstructuredPayload: true,
  ready: false,
  firstBlockingReason: 'external_processing_denied',
  // Root causes only. The ceiling row below is shut solely because control 4 is, and counting
  // it made this read "3 controls blocking" over two settings and a consequence.
  blockingCount: 3,
  warningCount: 0,
  evaluatedOnUtc: '2026-08-12T09:00:00Z',
  checks: [
    check(1, 'endpoint_resolved', 'Inference endpoint resolves'),
    check(2, 'policy_present', 'AI processing policy exists'),
    check(3, 'policy_enabled', 'AI processing is enabled'),
    check(4, 'external_processing_allowed', 'External processing is consented to', {
      status: 'Fail',
      denialReason: 'external_processing_denied',
      currentValue: 'ExternalProcessingAllowed = false',
      requiredValue: 'ExternalProcessingAllowed = true',
      setItIn: 'PUT /api/platform/tenants/{id}/ai-policy (platform Owner, second factor required)',
      detail: 'Ships FALSE by default and stays false until a named Owner turns it on.',
    }),
    check(8, 'egress_policy_whole_document', 'Egress policy permits whole documents', {
      status: 'Fail',
      denialReason: 'egress_policy_forbids_whole_documents',
      currentValue: 'EgressPolicy = "RedactedFieldsOnly"',
      requiredValue: 'EgressPolicy = "FullDocument"',
      setItIn: 'PUT /api/platform/tenants/{id}/ai-policy (platform Owner, second factor required)',
      detail: 'Exactly "FullDocument" opts in, compared trimmed and case-insensitively.',
    }),
    check(12, 'policy_model_allowed', 'Policy allows this model', {
      status: 'Fail',
      denialReason: 'model_denied',
      currentValue: 'AllowedModel = "Deepseek-V4-Pro"',
      requiredValue: 'AllowedModel = "deepseek-v4-pro" (or unset)',
      setItIn: 'PUT /api/platform/tenants/{id}/ai-policy (platform Owner, second factor required)',
      detail: 'AllowedModel is compared ORDINAL — CASE-SENSITIVE — while AllowedProvider is not.',
    }),
    check(13, 'external_dependency_ceiling', 'External dependency ceiling', {
      status: 'Blocked',
      currentValue: 'waiting on control 4 (External processing is consented to) — once that is'
        + ' open, this destination\'s own grant exempts the call from the ratio.',
      detail: 'Governs UNAUTHORIZED external usage only, as a share of the last 100 governed calls.',
    }),
  ],
  ...overrides,
});

/** One control's card, addressed the way an operator finds it: by the control's own title. */
const row = (title: string) => screen.getByText(title).closest('.MuiPaper-root') as HTMLElement;

/**
 * The fourteen-control report is no longer the first thing on the tab — it is incident triage,
 * one disclosure down. Every assertion about it goes through the same click an engineer makes.
 */
const openTechnicalDetail = async () => {
  fireEvent.click(await screen.findByText(/Technical detail —/));
  await screen.findByText(/never changes anything and offers no control that would/i);
};

const verdict = () =>
  screen.getByText(/Documents will (not )?extract/).closest('.MuiAlert-root') as HTMLElement;

const renderTab = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><AiGovernanceTab tenant={{ id: '9', name: 'Acme' } as Tenant} /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'getTenantAiPolicy').mockResolvedValue(policy);
  vi.spyOn(platformApi, 'getTenantAiProviders').mockResolvedValue({
    resolvedProvider,
    resolvedProviderIsAuthorizedForUnstructured: false,
    resolvedProviderDecisionReason: 'external_endpoint_not_authorized',
    authorizations: [],
  });
  vi.spyOn(platformApi, 'getTenantAiReadiness').mockResolvedValue(readiness());
});

describe('AiGovernanceTab', () => {
  it('renders policy evidence and the resolved provider with Owner controls', async () => {
    renderTab();

    expect(await screen.findByText('Effective AI policy')).toBeVisible();
    expect(screen.getAllByText(/https:\/\/ollama\.example/).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Edit policy' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Authorize provider' })).toBeVisible();
    expect(platformApi.getTenantAiPolicy).toHaveBeenCalledWith('9');
    expect(platformApi.getTenantAiProviders).toHaveBeenCalledWith('9');
  });
});

describe('extraction pre-flight', () => {
  it('answers whether documents extract before anything has to be read', async () => {
    renderTab();

    // Above the fold: a sentence, not a denial code.
    expect(await screen.findByText(/Document reading is not working — 3 settings to change/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Set up AI' })).toBeVisible();
    expect(screen.queryByText('external_processing_denied')).not.toBeInTheDocument();

    // The verdict, the count, and the code the next submitted document comes back with, for
    // whoever needs to paste it into a ticket.
    await openTechnicalDetail();
    expect(screen.getByText(/Documents will not extract — 3 settings to change/)).toBeVisible();
    expect(within(verdict()).getByText('external_processing_denied')).toBeVisible();
    expect(within(verdict()).getByText(/fixing one reveals the next/i)).toBeVisible();
    expect(platformApi.getTenantAiReadiness).toHaveBeenCalledWith('9');
  });

  it('names every blocking control at once, with what to set and where', async () => {
    renderTab();

    await openTechnicalDetail();

    // Three closed controls in three different layers, each carrying the code the layer that
    // refuses actually emits. The gate itself can only ever report the first of them.
    const closed: [string, string, string][] = [
      ['External processing is consented to', 'external_processing_denied', 'ExternalProcessingAllowed = true'],
      ['Egress policy permits whole documents', 'egress_policy_forbids_whole_documents', 'EgressPolicy = "FullDocument"'],
      ['Policy allows this model', 'model_denied', 'AllowedModel = "deepseek-v4-pro" (or unset)'],
    ];
    for (const [title, denial, required] of closed) {
      const card = row(title);
      expect(within(card).getByText('Blocking')).toBeVisible();
      expect(within(card).getByText(denial)).toBeVisible();
      expect(within(card).getByText(required)).toBeVisible();
      expect(within(card).getByText(/Set it in: PUT \/api\/platform\/tenants/)).toBeVisible();
    }

    // A satisfied control states its verdict and gives nothing to act on.
    const satisfied = row('AI processing is enabled');
    expect(within(satisfied).getByText('Satisfied')).toBeVisible();
    expect(within(satisfied).queryByText('Required value')).not.toBeInTheDocument();
  });

  it('reports a control that is only waiting on another, without asking for anything', async () => {
    renderTab();

    await openTechnicalDetail();

    // The row an operator was previously sent to act on: red, counted, and instructing them to
    // "authorize this destination (controls 5-7)" when those controls already read Satisfied.
    // Addressed by its stated reason rather than its title: "External dependency ceiling" is
    // also a field label in the effective-policy grid further down the tab.
    const ceiling = screen.getByText(/waiting on control 4/).closest('.MuiPaper-root') as HTMLElement;
    expect(within(ceiling).getByText('Not reached')).toBeVisible();
    expect(within(ceiling).queryByText('Blocking')).not.toBeInTheDocument();
    expect(within(ceiling).queryByText('Required value')).not.toBeInTheDocument();
    expect(within(ceiling).queryByText(/Set it in/)).not.toBeInTheDocument();
  });

  it('says a tenant with no spending ceiling is ready and still owes a decision', async () => {
    vi.spyOn(platformApi, 'getTenantAiReadiness').mockResolvedValue(readiness({
      ready: true,
      firstBlockingReason: null,
      blockingCount: 0,
      warningCount: 1,
      checks: [
        check(14, 'monthly_hard_token_budget', 'Monthly token budget has headroom', {
          status: 'Warn',
          currentValue: 'MonthlyHardTokenLimit = (unset — no monthly ceiling: this tenant\'s AI'
            + ' spend is unbounded)',
          detail: 'An UNSET limit warns rather than passes.',
        }),
      ],
    }));

    renderTab();

    // Green said "finished" over a tenant whose AI spend nobody had put a number on.
    await openTechnicalDetail();
    expect(screen.getByText(/Documents will extract — 1 thing still to decide/)).toBeVisible();
    const budget = row('Monthly token budget has headroom');
    expect(within(budget).getByText('Needs a decision')).toBeVisible();
    expect(within(budget).getByText(/spend is unbounded/)).toBeVisible();
    expect(within(budget).queryByText('Satisfied')).not.toBeInTheDocument();
  });

  it('offers copy rather than retyping for the case-sensitive model comparison', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    renderTab();

    await openTechnicalDetail();
    const model = row('Policy allows this model');
    expect(within(model).getByText(/CASE-SENSITIVE/)).toBeVisible();

    fireEvent.click(within(model).getByRole('button', { name: 'Copy' }));

    // Byte for byte, including the lower-cased id the ORDINAL comparison demands.
    expect(writeText).toHaveBeenCalledWith('AllowedModel = "deepseek-v4-pro" (or unset)');
    expect(await within(model).findByText('Copied exactly as shown.')).toBeVisible();
  });

  it('keeps the report itself a diagnosis, with the action beside it rather than inside it', async () => {
    renderTab();

    await openTechnicalDetail();

    // No row carries a "fix this" affordance: the report still states, and never remediates.
    expect(screen.getByText(/never changes anything and offers no control that would/i)).toBeVisible();
    const closed = row('External processing is consented to');
    expect(within(closed).queryByRole('button', { name: /fix|allow|enable|apply/i })).not.toBeInTheDocument();
  });

  it('greys the egress controls on a local deployment instead of ticking them', async () => {
    vi.spyOn(platformApi, 'getTenantAiReadiness').mockResolvedValue(readiness({
      resolvedProvider: {
        ...resolvedProvider, endpoint: 'http://127.0.0.1:11434', model: 'qwen2.5:14b',
        providerClass: 'Local', classificationReason: 'loopback_endpoint',
      },
      ready: true,
      firstBlockingReason: null,
      blockingCount: 0,
      checks: [
        check(3, 'policy_enabled', 'AI processing is enabled'),
        check(8, 'egress_policy_whole_document', 'Egress policy permits whole documents', {
          status: 'NotApplicable',
          currentValue: 'Provider class Local (loopback_endpoint) — nothing egresses',
          detail: 'Not applicable to this deployment: it neither blocks nor counts as ready.',
        }),
      ],
    }));

    renderTab();

    await openTechnicalDetail();
    expect(screen.getByText('Documents will extract')).toBeVisible();
    const egress = row('Egress policy permits whole documents');
    expect(within(egress).getByText('Not applicable')).toBeVisible();
    expect(within(egress).getByText(/nothing egresses/)).toBeVisible();
    expect(within(egress).queryByText('Satisfied')).not.toBeInTheDocument();
  });

  it('sets up AI from four answers, and sends no endpoint or model id anywhere near the caller', async () => {
    const setup = vi.spyOn(platformApi, 'setTenantAiEnablement').mockResolvedValue({
      policy: { ...policy, isEnabled: true, externalProcessingAllowed: true, version: 3 },
      readiness: readiness({ ready: true, firstBlockingReason: null, blockingCount: 0, checks: [] }),
      authorization: null,
    });

    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Set up AI' }));

    // Q1 and Q1b are the consent decision, in the words a customer would recognise.
    fireEvent.click(await screen.findByText('An approved cloud provider'));
    fireEvent.click(screen.getByText('The whole document'));
    expect(screen.getByText(/document text leaves their infrastructure/)).toBeVisible();

    // The plan sells Private extraction, so cloud is beyond it — and the dialog says so and asks
    // who agreed, rather than letting the operator meet a refusal after pressing Apply.
    expect(screen.getByText("Beyond this tenant's plan")).toBeVisible();
    expect(screen.getByText(/Plan internal-pilot-qa sells Private extraction/)).toBeVisible();

    // Apply stays shut until somebody says who approved it.
    const apply = screen.getByRole('button', { name: 'Apply and re-check' });
    expect(apply).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Justification/), {
      target: { value: 'Signed DPA ref INTF-2026-114, clause 4.2.' },
    });
    expect(apply).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Who approved going beyond the plan/), {
      target: { value: 'Pilot extension agreed with Intelliflow IT, ref INTF-114.' },
    });
    expect(apply).toBeEnabled();
    fireEvent.click(apply);

    await waitFor(() => expect(setup).toHaveBeenCalledTimes(1));
    const [tenantId, body] = setup.mock.calls[0];
    expect(tenantId).toBe('9');
    expect(body).toMatchObject({
      posture: 'ApprovedCloud',
      cloudEgress: 'FullDocument',
      purposes: ['RfqExtraction'],
      version: 2,
      planDeviationReason: 'Pilot extension agreed with Intelliflow IT, ref INTF-114.',
    });
    // The whole point: the destination is the server's to know. One capital letter in a model id
    // used to refuse every document this tenant submitted.
    expect(JSON.stringify(body)).not.toContain('deepseek');
    expect(JSON.stringify(body)).not.toContain('ollama');
  });

  it('will not let an operator leave the spend question unanswered', async () => {
    // Unbounded spend is a choice somebody makes, not a field somebody skips — so the dialog
    // always has one of the two selected and says what the chosen one costs.
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Set up AI' }));

    // Nothing is pre-ticked for a tenant with no settled answer, so the spend question appears
    // only once somebody has said what may read the documents.
    expect(screen.queryByText('No ceiling')).not.toBeInTheDocument();
    fireEvent.click(await screen.findByText('An approved cloud provider'));

    // Seeded from the tenant's own policy, and recomputed as the operator types — the number
    // an operator can weigh is documents, not tokens.
    expect(await screen.findByText(/About 1 document a month/)).toBeVisible();
    fireEvent.change(screen.getByLabelText('Monthly token ceiling'), { target: { value: '2000000' } });
    expect(screen.getByText(/About 111 documents a month/)).toBeVisible();

    fireEvent.click(screen.getByText('No ceiling'));
    expect(screen.getByText('Unlimited spend.')).toBeVisible();
    expect(screen.getByText(/standing warning until somebody sets a number/)).toBeVisible();
  });

  it('offers private mode only where the deployment can honour it', async () => {
    // On an installation whose every inference destination is off-host, "nothing leaves their
    // servers" is Off with extra steps: it refuses every document under a code that reads like
    // a fault. The dialog says so instead of writing it.
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Set up AI' }));

    const privateMode = await screen.findByText(/A private model/);
    expect(within(privateMode.closest('label') as HTMLElement).getByRole('radio')).toBeDisabled();
    expect(screen.getByText(/Not available here: this installation is pointed at https:\/\/ollama.example/))
      .toBeVisible();
  });

  it('says the pre-flight is unknown rather than ready when it cannot be read, and still edits', async () => {
    vi.spyOn(platformApi, 'getTenantAiReadiness').mockRejectedValue(new Error('gateway timeout'));

    renderTab();

    await waitFor(() =>
      expect(screen.getByText('The pre-flight could not be read')).toBeVisible());
    expect(screen.getByText(/not the same as them being ready/i)).toBeVisible();
    // The operator can still act; they simply are not told what to act on.
    expect(screen.getByRole('button', { name: 'Edit policy' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Authorize provider' })).toBeVisible();
  });
});
