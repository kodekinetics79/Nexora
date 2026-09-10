import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import { NO_CONCERN_RATIONALE } from './decideRules';

/**
 * What a PERSON sees on the one-screen decision: who is asking, one choice per line, one sentence
 * naming the next thing, one button. And what the button does behind their back: the fit
 * assessment, the committed decision, the lifecycle qualification and the promotion, in order.
 */

const auth = { user: { id: 7, isManager: true, isSuperAdmin: false, businessUnitId: 1 } };
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: auth.user, hasPermission: () => true }),
}));

const snack = vi.fn();
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: snack }) }));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

const api = {
  getWorkbench: vi.fn(),
  saveFitAssessment: vi.fn(),
  saveParticipation: vi.fn(),
  promoteToRfq: vi.fn(),
  resolveRfqRevisionImpact: vi.fn(),
  getDecisionBrief: vi.fn(),
  getState: vi.fn(),
  transition: vi.fn(),
};
vi.mock('../../../api/services/leadDecisionService', () => ({
  default: {
    getWorkbench: (...args: unknown[]) => api.getWorkbench(...args),
    saveFitAssessment: (...args: unknown[]) => api.saveFitAssessment(...args),
    saveParticipation: (...args: unknown[]) => api.saveParticipation(...args),
    promoteToRfq: (...args: unknown[]) => api.promoteToRfq(...args),
    resolveRfqRevisionImpact: (...args: unknown[]) => api.resolveRfqRevisionImpact(...args),
  },
}));
vi.mock('../../../api/services/decisionService', () => ({
  default: { getDecisionBrief: (...args: unknown[]) => api.getDecisionBrief(...args) },
}));
vi.mock('../../../api/services/commercialLifecycleService', () => ({
  default: {
    getState: (...args: unknown[]) => api.getState(...args),
    transition: (...args: unknown[]) => api.transition(...args),
  },
}));
// The customer picker is its own tested screen; here it only needs to be reachable.
vi.mock('../ResolveClientDialog', () => ({
  default: ({ open }: { open: boolean }) => (open ? <div role="dialog" aria-label="Choose customer">customer picker</div> : null),
}));
vi.mock('../Workbench/SourceEvidencePanel', () => ({ default: () => <h2>Source evidence</h2> }));

import DecidePage from './DecidePage';

const line = (overrides: Partial<LeadDecisionLineDTO> & { id: number }): LeadDecisionLineDTO => ({
  revisionLineId: overrides.id * 10,
  lineItemNo: String(overrides.id).padStart(5, '0'),
  description: `Item ${overrides.id}`,
  quantity: 4,
  unitOfMeasure: 'EA',
  currency: 'SAR',
  verificationStatus: 'VERIFIED',
  ...overrides,
});

const baseWorkbench = (): LeadDecisionWorkbenchDTO => ({
  leadId: 407,
  leadRevisionId: 9001,
  leadRevisionNumber: 1,
  decisionVersion: 3,
  participationVersion: null,
  participationStatus: 'NONE',
  lifecycleStatusCode: 'RECEIVED',
  customerId: 30,
  customerName: 'Saudi Electricity Company',
  customerRfqReference: 'SEC-RFQ-4471182',
  receivedAtUtc: '2026-09-09T07:42:11Z',
  bidClosingDate: '2099-09-18T00:00:00Z',
  hasFrozenCommercialHeader: true,
  verificationStatus: 'VERIFIED',
  evidence: [],
  lines: [
    line({ id: 1, description: 'Metal-enclosed switchgear enclosure, 13.8 kV outdoor' }),
    line({ id: 2, description: 'Busbar support insulator, epoxy cast, 15 kV', currency: null }),
    line({ id: 3, description: 'Cable gland kit, brass, 95 mm2' }),
  ],
  reasonCodes: [{ code: 'NO_STOCK', label: 'Item unavailable', appliesTo: ['NoBid'] }],
  unitOptions: [{ code: 'EA', label: 'Each' }],
  currencyOptions: [{ code: 'SAR', label: 'Saudi riyal' }, { code: 'USD', label: 'US dollar' }],
  fitAssessment: {
    version: 0,
    overallDecision: 'CONDITIONAL',
    rationale: '',
    criteria: ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL']
      .map((code) => ({ code, label: code, decision: 'UNKNOWN' as const })),
  },
  promotion: null,
  blockers: [
    { code: 'FIT_REQUIRED', message: 'Save a human fit assessment for the current revision.' },
    { code: 'PARTICIPATION_REQUIRED', message: 'Save participation choices for every current revision line.' },
    { code: 'LEAD_NOT_ELIGIBLE', message: 'Only a qualified lead can be converted to an RFQ.' },
  ],
});

/** The server's record, mutated by the mocked writes so each refetch sees the new versions. */
let record: LeadDecisionWorkbenchDTO;

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/procurement/leads/407/workbench']}>
        <Routes>
          <Route path="/procurement/leads/:id/workbench" element={<DecidePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

const pickOption = async (comboboxName: string | RegExp, optionName: string | RegExp) => {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: comboboxName }));
  const list = await screen.findByRole('listbox');
  fireEvent.click(within(list).getByRole('option', { name: optionName }));
};

const quoteEveryLine = async () => {
  for (const label of ['00001', '00002', '00003']) {
    const group = await screen.findByRole('group', { name: `Quote or skip line ${label}` });
    fireEvent.click(within(group).getByRole('button', { name: 'Quote' }));
  }
};

const status = () => screen.getByRole('status', { name: 'Next step' });

beforeEach(() => {
  vi.clearAllMocks();
  auth.user = { id: 7, isManager: true, isSuperAdmin: false, businessUnitId: 1 };
  record = baseWorkbench();
  api.getWorkbench.mockImplementation(async () => structuredClone(record));
  api.getDecisionBrief.mockResolvedValue({
    recommendation: 'bid', coveragePct: 100, estimatedValue: 186400, daysLeft: 9, urgency: 'soon',
    reasons: ['3 of 3 lines match your catalogue.', '9 days to quote.'],
  });
  api.getState.mockResolvedValue({
    aggregateId: 407, currentStatusCode: 'RECEIVED', version: 2, isTerminal: false,
    allowedTransitions: [{ statusId: 8, statusCode: 'QUALIFIED', label: 'Qualified', requiresReason: false }],
  });
  api.saveFitAssessment.mockImplementation(async () => {
    record = {
      ...record,
      decisionVersion: record.decisionVersion + 1,
      fitAssessment: { version: 1, overallDecision: 'FIT', rationale: NO_CONCERN_RATIONALE, criteria: record.fitAssessment!.criteria.map((c) => ({ ...c, decision: 'PASS' as const })) },
      blockers: record.blockers.filter((b) => b.code !== 'FIT_REQUIRED'),
    };
    return record.fitAssessment;
  });
  api.saveParticipation.mockImplementation(async (_id: number, request: { commit: boolean }) => {
    record = {
      ...record,
      participationStatus: request.commit ? 'COMMITTED' : 'DRAFT',
      participationVersion: 1,
      blockers: record.blockers.filter((b) => b.code !== 'PARTICIPATION_REQUIRED'),
    };
    return { decisionVersion: record.decisionVersion, participationVersion: 1, participationStatus: record.participationStatus };
  });
  api.transition.mockResolvedValue({ newStatusCode: 'QUALIFIED' });
  api.promoteToRfq.mockResolvedValue({ rfqId: 417, rfqNumber: 'RFQ-2026-0417', leadRevisionNumber: 1, participationVersion: 1, promotedLineCount: 3, promotedAtUtc: '2026-09-09T09:00:00Z' });
});

describe('DecidePage', () => {
  it('shows who is asking, what Nexora thinks, and names one next thing at a time', async () => {
    renderPage();
    expect(await screen.findByRole('heading', { level: 1, name: 'Saudi Electricity Company' })).toBeInTheDocument();
    expect(await screen.findByText(/Nexora's read:/)).toBeInTheDocument();
    expect(screen.getByText('3 of 3 lines match your catalogue.')).toBeInTheDocument();

    expect(status()).toHaveTextContent('Choose Quote or Skip for line 00001.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeDisabled();

    await quoteEveryLine();
    expect(status()).toHaveTextContent('Choose the currency for line 00002.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeDisabled();

    await pickOption('Currency for line 00002', 'SAR');
    expect(status()).toHaveTextContent('Records the assessment, the decision and the RFQ together.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeEnabled();
  });

  it('chains the assessment, the decision, qualification and promotion behind one button', async () => {
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));

    await waitFor(() => expect(api.promoteToRfq).toHaveBeenCalled());

    const fitRequest = api.saveFitAssessment.mock.calls[0][1];
    expect(fitRequest.overallDecision).toBe('FIT');
    expect(fitRequest.rationale).toBe(NO_CONCERN_RATIONALE);
    expect(fitRequest.criteria).toHaveLength(5);
    expect(fitRequest.criteria.every((c: { decision: string }) => c.decision === 'PASS')).toBe(true);
    expect(api.saveFitAssessment.mock.calls[0][2]).toMatch(/^lead-fit:407:/);

    const participation = api.saveParticipation.mock.calls[0][1];
    expect(participation.commit).toBe(true);
    // The fit save bumped the decision version; the commit must quote the fresh one.
    expect(participation.expectedDecisionVersion).toBe(4);
    expect(participation.lines.map((l: { decision: string; currency: string }) => [l.decision, l.currency]))
      .toEqual([['Bid', 'SAR'], ['Bid', 'SAR'], ['Bid', 'SAR']]);

    expect(api.transition).toHaveBeenCalledWith('leads', 407, expect.objectContaining({ currentStatusCode: 'RECEIVED' }), expect.objectContaining({ statusCode: 'QUALIFIED' }));
    expect(api.promoteToRfq).toHaveBeenCalledWith(407, expect.objectContaining({
      expectedLeadRevisionId: 9001,
      expectedDecisionVersion: 4,
      expectedParticipationVersion: 1,
      idempotencyKey: expect.stringMatching(/^lead-promotion:407:9001:/),
    }));
    expect(snack).toHaveBeenCalledWith('RFQ RFQ-2026-0417 created with 3 lines.', { variant: 'success' });
    expect(navigate).toHaveBeenCalledWith('/procurement/rfqs/view/417');
  });

  it('lets a rep save for a manager and never offers Create RFQ', async () => {
    auth.user = { id: 9, isManager: false, isSuperAdmin: false, businessUnitId: 1 };
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    expect(screen.queryByRole('button', { name: 'Create RFQ' })).not.toBeInTheDocument();
    const save = screen.getByRole('button', { name: 'Save for a manager' });
    expect(status()).toHaveTextContent('A manager creates the RFQ.');
    fireEvent.click(save);

    await waitFor(() => expect(api.saveParticipation).toHaveBeenCalled());
    expect(api.saveParticipation.mock.calls[0][1].commit).toBe(false);
    expect(api.promoteToRfq).not.toHaveBeenCalled();
    expect(api.transition).not.toHaveBeenCalled();
    await waitFor(() => expect(snack).toHaveBeenCalledWith('Saved. A manager can create the RFQ from here.', { variant: 'success' }));
  });

  it('turns every-line-skipped into Decline request and records the governed reason', async () => {
    renderPage();
    for (const label of ['00001', '00002', '00003']) {
      const group = await screen.findByRole('group', { name: `Quote or skip line ${label}` });
      fireEvent.click(within(group).getByRole('button', { name: 'Skip' }));
    }
    expect(status()).toHaveTextContent('Say why line 00001 is skipped.');
    for (const label of ['00001', '00002', '00003']) {
      await pickOption(`Why skip line ${label}`, 'Item unavailable');
    }
    const decline = screen.getByRole('button', { name: 'Decline request' });
    expect(decline).toBeEnabled();
    fireEvent.click(decline);

    const dialog = await screen.findByRole('dialog', { name: 'Commit full no-bid' });
    fireEvent.mouseDown(within(dialog).getByRole('combobox', { name: 'Full no-bid reason' }));
    fireEvent.click(within(await screen.findByRole('listbox')).getByRole('option', { name: /Item unavailable/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Commit full no-bid' }));

    await waitFor(() => expect(api.saveParticipation).toHaveBeenCalled());
    const request = api.saveParticipation.mock.calls[0][1];
    expect(request.commit).toBe(true);
    expect(request.reasonCode).toBe('NO_STOCK');
    expect(request.lines.every((l: { decision: string; reasonCode: string }) => l.decision === 'NoBid' && l.reasonCode === 'NO_STOCK')).toBe(true);
    expect(api.promoteToRfq).not.toHaveBeenCalled();
  });

  it('records a concern for review instead of creating an RFQ', async () => {
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    fireEvent.click(screen.getByRole('button', { name: 'Yes, raise a concern' }));
    expect(status()).toHaveTextContent('Tick which part concerns you.');
    fireEvent.click(screen.getByRole('checkbox', { name: 'We may miss the delivery date' }));
    expect(status()).toHaveTextContent('Say what the concern is, in a few words.');
    fireEvent.change(screen.getByRole('textbox', { name: 'What is the concern?' }), { target: { value: 'Nine days is too tight for GE enclosures.' } });

    const save = screen.getByRole('button', { name: 'Save for review' });
    expect(screen.queryByRole('button', { name: 'Create RFQ' })).not.toBeInTheDocument();
    fireEvent.click(save);

    await waitFor(() => expect(api.saveParticipation).toHaveBeenCalled());
    const fit = api.saveFitAssessment.mock.calls[0][1];
    expect(fit.overallDecision).toBe('CONDITIONAL');
    expect(fit.rationale).toBe('Nine days is too tight for GE enclosures.');
    expect(fit.criteria.find((c: { code: string }) => c.code === 'DELIVERY')).toMatchObject({ decision: 'CONCERN', note: 'Nine days is too tight for GE enclosures.' });
    expect(fit.criteria.filter((c: { decision: string }) => c.decision === 'PASS')).toHaveLength(4);
    expect(api.saveParticipation.mock.calls[0][1].commit).toBe(false);
    expect(api.promoteToRfq).not.toHaveBeenCalled();
  });

  it('puts the missing customer first, with the picker one click away', async () => {
    record = { ...baseWorkbench(), customerId: null, customerName: null };
    renderPage();
    expect(await screen.findByRole('heading', { level: 1, name: 'Customer not matched yet' })).toBeInTheDocument();
    expect(status()).toHaveTextContent('Choose the customer this request came from.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Choose the customer' }));
    expect(await screen.findByRole('dialog', { name: 'Choose customer' })).toBeInTheDocument();
  });

  it('says an already promoted request is done and offers no second RFQ', async () => {
    record = {
      ...baseWorkbench(),
      participationStatus: 'COMMITTED',
      participationVersion: 1,
      promotion: { rfqId: 417, rfqNumber: 'RFQ-2026-0417', leadRevisionNumber: 1, participationVersion: 1, promotedLineCount: 3, promotedAtUtc: '2026-09-09T09:00:00Z', promotedBy: 'zack@kodekinetics.com' },
      blockers: [],
    };
    renderPage();
    expect(await screen.findByText('RFQ RFQ-2026-0417 created')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create RFQ' })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: /Quote or skip/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open the RFQ' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/rfqs/view/417');
  });

  it('never renders an empty form when the request cannot be loaded', async () => {
    api.getWorkbench.mockRejectedValue(new Error('boom'));
    renderPage();
    // The page retries the read once before it gives up, so the error outlives the default wait.
    expect(await screen.findByText('This request could not be loaded', undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create RFQ' })).not.toBeInTheDocument();
  });

  it('opens the history drawer when an RFQ links straight to the evidence', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/procurement/leads/407/workbench?stage=evidence']}>
          <Routes><Route path="/procurement/leads/:id/workbench" element={<DecidePage />} /></Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('heading', { name: 'Source evidence' })).toBeInTheDocument();
  });
});
