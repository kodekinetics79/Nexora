import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import { NO_CONCERN_RATIONALE } from './decideRules';
import { toPresentableError } from '../../../utils/apiErrors';

/**
 * What a PERSON sees on the one-screen decision: who is asking, one choice per line, one sentence
 * naming the next thing, one button. And what the button does behind their back: the fit
 * assessment, the committed decision, the lifecycle qualification and the promotion, in order.
 */

const auth: {
  user: { id: number; isManager: boolean; isSuperAdmin: boolean; businessUnitId: number; email?: string };
  hasPermission: (moduleName: string, action?: string) => boolean;
  stale: boolean;
} = { user: { id: 7, isManager: true, isSuperAdmin: false, businessUnitId: 1 }, hasPermission: () => true, stale: false };
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: auth.user, hasPermission: auth.hasPermission, permissionsStale: auth.stale, permissionsError: null }),
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
const getLead = vi.fn();
vi.mock('../../../api/services/leadService', () => ({
  default: { getById: (...args: unknown[]) => getLead(...args) },
}));
vi.mock('../LeadOwnerControl', () => ({
  default: ({ lockedReason }: { lockedReason?: string | null }) => (
    <div>
      <div>Owner control</div>
      {lockedReason ? <div>{lockedReason}</div> : null}
    </div>
  ),
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
// The document check is its own tested dialog; here it only needs to open on the right line.
vi.mock('./CheckDocumentDialog', () => ({
  default: ({ open, focusLineId }: { open: boolean; focusLineId?: number | null }) =>
    (open ? <div role="dialog" aria-label="Check against the document">focus:{String(focusLineId)}</div> : null),
}));

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
  reasonCodes: [{ code: 'NO_STOCK', label: 'Item unavailable', appliesTo: ['NoBid', 'Decline'] }],
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

const PROMOTION = { rfqId: 417, rfqNumber: 'RFQ-2026-0417', leadRevisionNumber: 1, participationVersion: 1, promotedLineCount: 3, promotedAtUtc: '2026-09-09T09:00:00Z', promotedBy: 'zack@kodekinetics.com' };

/** Every line as saved on the server with the given choice. */
const savedLines = (participation: NonNullable<LeadDecisionLineDTO['participation']>) =>
  baseWorkbench().lines.map((item) => ({ ...item, participation }));

const promotedRecord = (): LeadDecisionWorkbenchDTO => ({
  ...baseWorkbench(),
  participationStatus: 'COMMITTED',
  participationVersion: 1,
  lines: savedLines({ decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }),
  promotion: { ...PROMOTION },
  blockers: [],
});

const lifecycleOf = (overrides: Record<string, unknown>) => ({
  aggregateId: 407, currentStatusCode: 'RECEIVED', version: 3, isTerminal: false, allowedTransitions: [], ...overrides,
});

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const tree = () => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/procurement/leads/407/workbench']}>
        <Routes>
          <Route path="/procurement/leads/:id/workbench" element={<DecidePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(tree());
  return { ...view, client, rerenderPage: () => view.rerender(tree()) };
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
  sessionStorage.clear();
  auth.user = { id: 7, isManager: true, isSuperAdmin: false, businessUnitId: 1 };
  auth.hasPermission = () => true;
  auth.stale = false;
  record = baseWorkbench();
  getLead.mockResolvedValue({ id: 501, assignedToId: 7, assignedToFullName: 'Golden Salesperson', assignmentMethod: 'MANUAL', assignmentVersion: 1 });
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
    // A matched customer cannot be changed here: the server refuses it once resolved.
    expect(screen.queryByRole('button', { name: /Not them|Choose the customer/ })).not.toBeInTheDocument();
    expect(await screen.findByText(/Nexora's read:/)).toBeInTheDocument();
    // The same name the list gives the same read.
    expect(screen.getByText('Worth bidding')).toBeInTheDocument();
    expect(screen.getByText('3 of 3 lines match your catalogue.')).toBeInTheDocument();

    expect(status()).toHaveTextContent('Choose Quote or Skip for line 00001.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeDisabled();

    await quoteEveryLine();
    expect(status()).toHaveTextContent('Choose the currency for line 00002.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeDisabled();

    await pickOption('Currency for line 00002', 'SAR');
    expect(status()).toHaveTextContent('Ready: Create RFQ asks you to confirm, then puts 3 of 3 lines for Saudi Electricity Company into a new RFQ.');
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeEnabled();
  });

  it('chains the assessment, the decision, qualification and promotion behind one button', async () => {
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));

    // Create RFQ asks first; nothing is written until the person says yes.
    expect(api.saveFitAssessment).not.toHaveBeenCalled();
    const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
    expect(dialog).toHaveTextContent('3 of 3 lines go into the RFQ.');
    expect(dialog).toHaveTextContent('Yes also marks the request qualified.');
    expect(dialog).toHaveTextContent('Concern: none raised.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Yes, create the RFQ' }));

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
    // The server refuses to commit a Bid line on an unqualified lead, so qualify comes first.
    expect(api.transition.mock.invocationCallOrder[0]).toBeLessThan(api.saveParticipation.mock.invocationCallOrder[0]);
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

  it('does not offer a rep the decline the server would refuse; their skip-everything is saved for a manager', async () => {
    auth.user = { id: 9, isManager: false, isSuperAdmin: false, businessUnitId: 1 };
    renderPage();
    for (const label of ['00001', '00002', '00003']) {
      const group = await screen.findByRole('group', { name: `Quote or skip line ${label}` });
      fireEvent.click(within(group).getByRole('button', { name: 'Skip' }));
      await pickOption(`Why skip line ${label}`, 'Item unavailable');
    }
    expect(screen.queryByRole('button', { name: 'Decline request' })).not.toBeInTheDocument();
    expect(status()).toHaveTextContent('a manager declines the request');
    fireEvent.click(screen.getByRole('button', { name: 'Save for a manager' }));
    await waitFor(() => expect(api.saveParticipation).toHaveBeenCalled());
    expect(api.saveParticipation.mock.calls[0][1].commit).toBe(false);
    await waitFor(() => expect(snack).toHaveBeenCalledWith('Saved with every line skipped. A manager can decline the request from here.', { variant: 'success' }));
    expect(snack).not.toHaveBeenCalledWith('Saved. A manager can create the RFQ from here.', expect.anything());
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
    expect(status()).toHaveTextContent('A concern stops the RFQ. Save for review records it; no RFQ can be created while it stands.');
    fireEvent.click(save);

    await waitFor(() => expect(api.saveParticipation).toHaveBeenCalled());
    const fit = api.saveFitAssessment.mock.calls[0][1];
    expect(fit.overallDecision).toBe('CONDITIONAL');
    expect(fit.rationale).toBe('Nine days is too tight for GE enclosures.');
    expect(fit.criteria.find((c: { code: string }) => c.code === 'DELIVERY')).toMatchObject({ decision: 'CONCERN', note: 'Nine days is too tight for GE enclosures.' });
    expect(fit.criteria.filter((c: { decision: string }) => c.decision === 'PASS')).toHaveLength(4);
    expect(api.saveParticipation.mock.calls[0][1].commit).toBe(false);
    expect(api.promoteToRfq).not.toHaveBeenCalled();
    await waitFor(() => expect(snack).toHaveBeenCalledWith('Saved with your concern. No RFQ can be created while it stands.', { variant: 'success' }));
    expect(snack).not.toHaveBeenCalledWith('Saved. A manager can create the RFQ from here.', expect.anything());
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
    record = promotedRecord();
    renderPage();
    expect(await screen.findByText('Became an RFQ: RFQ-2026-0417')).toBeInTheDocument();
    // The lines read the saved record from the first paint: a line in the RFQ never says "Left out" first.
    expect(screen.queryByText('Left out')).toBeNull();
    expect(screen.getByText(/3 of 3 lines went into the RFQ\./)).toBeInTheDocument();
    expect(screen.queryByText(/Already promoted/)).toBeNull();
    await waitFor(() => expect(screen.getAllByText('Went into the RFQ')).toHaveLength(3));
    expect(screen.queryByText('Quoted')).toBeNull();
    expect(screen.getByRole('status', { name: 'Next step' })).toHaveTextContent('This request became RFQ RFQ-2026-0417. The work carries on from the RFQ.');
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

  it('opens the document check in place for a line Nexora is unsure about', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1, verificationStatus: 'NEEDS_CHECK' }), line({ id: 2 })] };
    renderPage();
    const group = await screen.findByRole('group', { name: 'Quote or skip line 00001' });
    fireEvent.click(within(group).getByRole('button', { name: 'Quote' }));
    fireEvent.click(within(await screen.findByRole('group', { name: 'Quote or skip line 00002' })).getByRole('button', { name: 'Quote' }));
    expect(status()).toHaveTextContent('Check what Nexora read for line 00001 against the document.');
    // The one button IS the next step: it reads "Check the document" until the check is done.
    expect(screen.queryByRole('button', { name: 'Create RFQ' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Check the document' })).toBeInTheDocument();

    // The button beside the sentence, the line's own link and the section header: all the same dialog.
    fireEvent.click(screen.getByRole('button', { name: 'Check the document' }));
    expect(await screen.findByRole('dialog', { name: 'Check against the document' })).toHaveTextContent('focus:null');
    expect(navigate).not.toHaveBeenCalledWith(expect.stringContaining('/procurement/extraction/review/'));
  });

  it('carries Quote and Skip choices across the new revision a document check creates', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1, verificationStatus: 'NEEDS_CHECK' }), line({ id: 2 })] };
    const { client } = renderPage();
    fireEvent.click(within(await screen.findByRole('group', { name: 'Quote or skip line 00001' })).getByRole('button', { name: 'Quote' }));
    fireEvent.click(within(screen.getByRole('group', { name: 'Quote or skip line 00002' })).getByRole('button', { name: 'Skip' }));
    await pickOption('Why skip line 00002', 'Item unavailable');

    // The server minted revision 2 with new line ids, both lines now verified; the page re-reads it.
    record = {
      ...record,
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      lines: [line({ id: 7, lineItemNo: '00001' }), line({ id: 8, lineItemNo: '00002' })],
    };
    await client.invalidateQueries({ queryKey: ['lead-decision-workbench', 407] });
    await screen.findByText('Revision 2');
    const first = await screen.findByRole('group', { name: 'Quote or skip line 00001' });
    await waitFor(() => expect(within(first).getByRole('button', { name: 'Quote' })).toHaveAttribute('aria-pressed', 'true'));
    expect(within(screen.getByRole('group', { name: 'Quote or skip line 00002' })).getByRole('button', { name: 'Skip' })).toHaveAttribute('aria-pressed', 'true');
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


describe('one decision for the whole request', () => {
  it('Quote all quotes every line in one click', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));

    for (const label of ['00001', '00002', '00003']) {
      const group = screen.getByRole('group', { name: `Quote or skip line ${label}` });
      expect(within(group).getByRole('button', { name: 'Quote' })).toHaveAttribute('aria-pressed', 'true');
    }
    expect(screen.getByText(/3 lines marked to quote/).textContent).toMatch(/^3 of 3/);
  });

  it('Skip all asks for one reason and puts it on every line', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Skip all…' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Item unavailable' }));

    for (const label of ['00001', '00002', '00003']) {
      const group = screen.getByRole('group', { name: `Quote or skip line ${label}` });
      expect(within(group).getByRole('button', { name: 'Skip' })).toHaveAttribute('aria-pressed', 'true');
    }
    expect(screen.getAllByRole('combobox', { name: /Why skip line/ })).toHaveLength(3);
    for (const box of screen.getAllByRole('combobox', { name: /Why skip line/ }))
      expect(box).toHaveTextContent('Item unavailable');
  });

  it('Quote all writes the acknowledgement a warned line needs, where it can be changed', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1 }), line({ id: 2, needsAttention: true, attentionReason: 'No catalog match found' })] };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));

    const note = screen.getByRole('textbox', { name: 'How you handled it (line 00002)' });
    expect(note).toHaveValue('Quoted as read; no catalogue match yet, sourcing will resolve it.');
    expect(screen.queryByRole('textbox', { name: 'How you handled it (line 00001)' })).toBeNull();
  });

  it('a line skipped after Quote all keeps its own decision', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    const group = screen.getByRole('group', { name: 'Quote or skip line 00002' });
    fireEvent.click(within(group).getByRole('button', { name: 'Skip' }));

    expect(within(group).getByRole('button', { name: 'Skip' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText(/lines marked to quote/).textContent).toMatch(/^2 of 3/);
  });

  it('holds the decision until the request has an owner, and says so', async () => {
    // Upload, assign, decide — in that order. A request nobody owns shows its lines but takes no
    // decision, and the one sentence next to the button says what to do instead.
    getLead.mockResolvedValue({ id: 501, assignedToId: null, assignedToFullName: null, assignmentVersion: 1 });
    renderPage();

    await screen.findByText(/Who's on it — nobody yet/);
    expect(screen.getByText('Owner control')).toBeInTheDocument();
    expect(await screen.findByRole('status', { name: 'Next step' })).toHaveTextContent('Assign an owner first');
    // The button itself is the next step, not a grey "Create RFQ".
    expect(screen.queryByRole('button', { name: /Create RFQ|Save for a manager|Save for review/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Assign an owner' })).toBeEnabled();
  });
});

describe('a request that gives no unit', () => {
  it('asks for the unit on the line, takes the rep to the picker, and the choice clears the step', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1, unitOfMeasure: null }), line({ id: 2 })] };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    expect(status()).toHaveTextContent('The request gives no unit for line 00001. Choose it beside the quantity.');
    expect(screen.getByText('not stated in the request')).toBeInTheDocument();

    // One control named for the step — the button beside the sentence — not a second link.
    fireEvent.click(screen.getByRole('button', { name: 'Choose the unit' }));
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveFocus());
    await pickOption('Unit for line 00001', 'EA');
    expect(status()).toHaveTextContent('Ready: Create RFQ asks you to confirm, then puts 2 of 2 lines for Saudi Electricity Company into a new RFQ.');
  });

  it('sets one unit on every quoted line without one from a single picker', async () => {
    record = { ...baseWorkbench(), lines: [1, 2, 3].map((id) => line({ id, unitOfMeasure: null })) };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    expect(status()).toHaveTextContent('3 lines marked to quote need a unit. Choose one for all 3 above the lines, or line by line.');
    fireEvent.click(screen.getByRole('button', { name: 'Choose the unit' }));
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Unit for the 3 lines marked to quote without one' })).toHaveFocus());

    await pickOption('Unit for the 3 lines marked to quote without one', /^EA/);
    for (const label of ['00001', '00002', '00003']) {
      expect(screen.getByRole('combobox', { name: `Unit for line ${label}` })).toHaveTextContent('EA');
    }
    expect(status()).toHaveTextContent('Ready: Create RFQ asks you to confirm, then puts 3 of 3 lines for Saudi Electricity Company into a new RFQ.');
  });

  it('keeps the unit the rep chose when the document check creates a new revision without one', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1, unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' }), line({ id: 2 })] };
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    await pickOption('Unit for line 00001', 'EA');
    expect(status()).toHaveTextContent('Check line 00001 against the document and confirm it. The unit you chose goes with it.');

    record = {
      ...record,
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      lines: [line({ id: 7, lineItemNo: '00001', unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' }), line({ id: 8, lineItemNo: '00002' })],
    };
    await client.invalidateQueries({ queryKey: ['lead-decision-workbench', 407] });
    await screen.findByText('Revision 2');
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveTextContent('EA'));
    expect(status()).toHaveTextContent('Check line 00001 against the document and confirm it. The unit you chose goes with it.');
  });

  it('lets a unit corrected on the new revision win over the earlier pick', async () => {
    record = {
      ...baseWorkbench(),
      unitOptions: [{ code: 'EA', label: 'Each' }, { code: 'SET', label: 'Set' }],
      lines: [line({ id: 1, unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' }), line({ id: 2 })],
    };
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    await pickOption('Unit for line 00001', 'EA');

    record = { ...record, leadRevisionId: 9002, leadRevisionNumber: 2, lines: [line({ id: 7, lineItemNo: '00001', unitOfMeasure: 'SET' }), line({ id: 8, lineItemNo: '00002' })] };
    await client.invalidateQueries({ queryKey: ['lead-decision-workbench', 407] });
    await screen.findByText('Revision 2');
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveTextContent('SET'));
  });

  it('writes what happened to the unit on a warned line, not a catalogue miss, and keeps it true as the unit is chosen', async () => {
    record = { ...baseWorkbench(), lines: [line({ id: 1, unitOfMeasure: null, needsAttention: true, attentionReason: 'Unit of measure missing' }), line({ id: 2 })] };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    expect(screen.getByRole('textbox', { name: 'How you handled it (line 00001)' })).toHaveValue('The request gave no unit; the unit is chosen on the line.');
    await pickOption('Unit for line 00001', 'EA');
    expect(screen.getByRole('textbox', { name: 'How you handled it (line 00001)' })).toHaveValue('The request gave no unit; quoted in EA.');
  });

  it('brings unsaved choices back onto the new revision an extraction approval created', async () => {
    sessionStorage.setItem('nexora.lead-decision.407', JSON.stringify({
      savedAt: '2026-09-12T08:00:00Z',
      value: {
        revisionId: 9001,
        decisions: { '00001': { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }, '00002': { decision: 'NoBid', reasonCode: 'NO_STOCK' } },
        concern: { raised: false, codes: [], note: '' },
      },
    }));
    record = {
      ...baseWorkbench(),
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      lines: [line({ id: 7, lineItemNo: '00001', unitOfMeasure: null }), line({ id: 8, lineItemNo: '00002' })],
    };
    renderPage();
    await waitFor(() => expect(within(screen.getByRole('group', { name: 'Quote or skip line 00001' })).getByRole('button', { name: 'Quote' }))
      .toHaveAttribute('aria-pressed', 'true'));
    expect(within(screen.getByRole('group', { name: 'Quote or skip line 00002' })).getByRole('button', { name: 'Skip' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveTextContent('EA');
    expect(snack).toHaveBeenCalledWith(expect.stringMatching(/^Restored the choices you had not saved yet/), { variant: 'info' });
  });
});

describe('background work never tears down the decision', () => {
  it('keeps the decision editable while the session re-reads its permissions', async () => {
    const view = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    expect(screen.getByRole('combobox', { name: 'Currency for line 00002' })).toBeInTheDocument();

    // The minute timer fires: the snapshot is marked stale and every edit grant reads as withdrawn
    // until the server answers.
    auth.stale = true;
    auth.hasPermission = (_moduleName, action = 'view') => action === 'view';
    view.rerenderPage();

    expect(screen.getByRole('combobox', { name: 'Currency for line 00002' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Quote all' })).toBeInTheDocument();
    expect(status()).not.toHaveTextContent('Your role can view this request but not decide it.');
  });

  it('applies a settled revocation at once', async () => {
    const view = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Quote all' }));
    auth.stale = false;
    auth.hasPermission = (_moduleName, action = 'view') => action === 'view';
    view.rerenderPage();
    expect(status()).toHaveTextContent('Your role can view this request but not decide it.');
    expect(screen.queryByRole('combobox', { name: 'Currency for line 00002' })).toBeNull();
  });

  it('keeps the lines and the rep\'s choices when a background re-read fails', async () => {
    const { client } = renderPage();
    fireEvent.click(within(await screen.findByRole('group', { name: 'Quote or skip line 00001' })).getByRole('button', { name: 'Quote' }));
    api.getWorkbench.mockRejectedValue(new Error('502 Bad Gateway'));
    await client.invalidateQueries({ queryKey: ['lead-decision-workbench', 407] });

    expect(await screen.findByText(/Couldn't refresh this request just now/, undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.queryByText('This request could not be loaded')).toBeNull();
    expect(within(screen.getByRole('group', { name: 'Quote or skip line 00001' })).getByRole('button', { name: 'Quote' }))
      .toHaveAttribute('aria-pressed', 'true');
  });
});


/** Takes a ready request to the question and says yes. */
const createRfqAndConfirm = async () => {
  await quoteEveryLine();
  await pickOption('Currency for line 00002', 'SAR');
  fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
  const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Yes, create the RFQ' }));
};

describe('Create RFQ asks first', () => {
  it('writes nothing when the person goes back', async () => {
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Go back' }));

    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Create an RFQ/ })).toBeNull());
    expect(api.saveFitAssessment).not.toHaveBeenCalled();
    expect(api.transition).not.toHaveBeenCalled();
    expect(api.saveParticipation).not.toHaveBeenCalled();
    expect(api.promoteToRfq).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeEnabled();
  });

  it('says what is still in the way when Create RFQ cannot be pressed yet', async () => {
    renderPage();
    await screen.findByRole('group', { name: 'Quote or skip line 00001' });
    const create = screen.getByRole('button', { name: 'Create RFQ' });
    expect(create).toBeDisabled();
    // A disabled button takes no hover, so the call-out sits on its wrapper.
    expect(create.parentElement).toHaveAttribute('title', 'Available once the step beside it is done.');
  });

  it('says so when the status could not be read, and lets the manager read it again', async () => {
    api.getState.mockRejectedValue(new Error('502'));
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    await waitFor(() => expect(status()).toHaveTextContent("Nexora couldn't read this request's status, so it can't be marked qualified here. Check the status again before you create the RFQ."));
    // The gate is unchanged: the button is still enabled.
    expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeEnabled();
    const calls = api.getState.mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Check the status again' }));
    await waitFor(() => expect(api.getState.mock.calls.length).toBeGreaterThan(calls));

    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
    expect(dialog).toHaveTextContent("Nexora couldn't read the request's status, so it isn't marked qualified here.");
  });

  // A re-read of the status runs after every save and whenever the window regains focus. When one
  // fails while the last good status is still held, Yes still qualifies the request from that
  // status, so the page must not say it can't.
  it('keeps the status it already read when a later re-read of it fails', async () => {
    const { client } = renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    const ready = 'Ready: Create RFQ asks you to confirm, then puts 3 of 3 lines for Saudi Electricity Company into a new RFQ.';
    await waitFor(() => expect(status()).toHaveTextContent(ready));

    api.getState.mockRejectedValue(new Error('502'));
    await client.invalidateQueries({ queryKey: ['lifecycle', 'leads', 407] });
    await waitFor(() => expect(client.getQueryState(['lifecycle', 'leads', 407])?.status).toBe('error'));
    expect(client.getQueryData(['lifecycle', 'leads', 407])).toBeDefined();

    expect(status()).toHaveTextContent(ready);
    expect(screen.queryByText(/couldn't read this request's status/i)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Check the status again' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
    expect(dialog).toHaveTextContent('Yes also marks the request qualified.');
    expect(dialog).not.toHaveTextContent(/couldn't read/i);
  });

  it('says the status is still being read, not that it failed, when Create RFQ is pressed before the first read arrives', async () => {
    api.getState.mockReturnValue(new Promise(() => undefined));
    renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create RFQ' })).toBeEnabled());

    expect(screen.queryByText(/couldn't read this request's status/i)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' });
    expect(dialog).toHaveTextContent("Nexora is still reading the request's status, so Yes won't mark it qualified.");
    expect(dialog).not.toHaveTextContent(/couldn't read/i);
    expect(dialog).not.toHaveTextContent('Yes also marks the request qualified.');
  });

  it('closes the question, and writes nothing, when the request stops being ready behind it', async () => {
    const { client } = renderPage();
    await quoteEveryLine();
    await pickOption('Currency for line 00002', 'SAR');
    fireEvent.click(screen.getByRole('button', { name: 'Create RFQ' }));
    expect(await screen.findByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' })).toBeInTheDocument();

    // Someone else checked the document while the question was open: a new revision arrives with a
    // line Nexora is unsure about, so Create RFQ is no longer the next thing.
    record = {
      ...record,
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      lines: [line({ id: 7, lineItemNo: '00001', verificationStatus: 'NEEDS_CHECK' }), line({ id: 8, lineItemNo: '00002' }), line({ id: 9, lineItemNo: '00003' })],
    };
    await client.invalidateQueries({ queryKey: ['lead-decision-workbench', 407] });

    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Create an RFQ/ })).toBeNull());
    expect(status()).toHaveTextContent('Check what Nexora read for line 00001 against the document.');
    expect(screen.queryByRole('button', { name: 'Yes, create the RFQ' })).toBeNull();
    expect(api.saveFitAssessment).not.toHaveBeenCalled();
    expect(api.transition).not.toHaveBeenCalled();
    expect(api.saveParticipation).not.toHaveBeenCalled();
    expect(api.promoteToRfq).not.toHaveBeenCalled();
  });
});

describe('a click that stops half-way says what went through', () => {
  it('keeps "Nothing was changed" for a click that wrote nothing', async () => {
    api.saveFitAssessment.mockRejectedValue(new Error('boom'));
    renderPage();
    await createRfqAndConfirm();
    await waitFor(() => expect(snack).toHaveBeenCalledWith('That did not go through. Nothing was changed.', { variant: 'error' }));
  });

  it('says the concern answer is saved when qualifying the request failed', async () => {
    api.transition.mockRejectedValue(new Error('boom'));
    renderPage();
    await createRfqAndConfirm();
    await waitFor(() => expect(snack).toHaveBeenCalledWith(
      'The RFQ was not created. Your concern answer is saved, but the request is not marked qualified and your line choices are not saved. Press Create RFQ again.',
      { variant: 'error' },
    ));
    expect(api.saveParticipation).not.toHaveBeenCalled();
  });

  it('says the choices are saved and the request qualified when only the RFQ failed', async () => {
    api.promoteToRfq.mockRejectedValue(new Error('boom'));
    renderPage();
    await createRfqAndConfirm();
    await waitFor(() => expect(snack).toHaveBeenCalledWith(
      'The RFQ was not created. Your choices are saved and the request is marked qualified. Press Create RFQ again.',
      { variant: 'error' },
    ));
    expect(snack).not.toHaveBeenCalledWith(expect.stringMatching(/Nothing was changed/), expect.anything());
  });

  it('adds the server\'s reason when the server refused the step', async () => {
    const refusal = { response: { status: 409, data: 'The decision changed since you opened it. Refresh and try again.' } };
    api.promoteToRfq.mockRejectedValue(refusal);
    renderPage();
    await createRfqAndConfirm();
    await waitFor(() => expect(snack).toHaveBeenCalledWith(
      `The RFQ was not created. Your choices are saved and the request is marked qualified. Press Create RFQ again. ${toPresentableError(refusal).message}`,
      { variant: 'error' },
    ));
  });
});

describe('a request that can no longer be decided reads as what it is', () => {
  it('says a promoted request is done even while its status is still being read', async () => {
    record = promotedRecord();
    api.getState.mockReturnValue(new Promise(() => undefined));
    renderPage();
    expect(await screen.findByText('Became an RFQ: RFQ-2026-0417')).toBeInTheDocument();
    expect(status()).toHaveTextContent('This request became RFQ RFQ-2026-0417. The work carries on from the RFQ.');
    expect(screen.getAllByRole('status', { name: 'Next step' })).toHaveLength(1);
  });

  it('names the person who created the RFQ as "You" when it was the viewer', async () => {
    auth.user = { ...auth.user, email: 'Zack@KodeKinetics.com' };
    record = promotedRecord();
    renderPage();
    expect(await screen.findByText(/^You created it on .+\. 3 of 3 lines went into the RFQ\.$/)).toBeInTheDocument();
    expect(screen.queryByText(/zack@kodekinetics\.com/)).toBeNull();
    // Decided, so the owner is changed on the lead page, and the screen says so.
    expect(screen.getByText('This request is decided, so its owner is changed on the lead page.')).toBeInTheDocument();
  });

  it('says a declined request is finished, with the skipped total, before any status sentence', async () => {
    record = { ...baseWorkbench(), participationStatus: 'COMMITTED', participationVersion: 2, lines: savedLines({ decision: 'NoBid', reasonCode: 'NO_STOCK' }), blockers: [] };
    api.getState.mockResolvedValue(lifecycleOf({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, canReopen: true }));
    renderPage();
    expect(await screen.findByText('Request declined')).toBeInTheDocument();
    await waitFor(() => expect(status()).toHaveTextContent('Every line was skipped and the request was declined. No RFQ was created, and there is nothing more to do here.'));
    expect(screen.getByText('3 of 3 lines skipped')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open the lead' })).toBeNull();
  });

  it('reads a request closed as a duplicate as text, with one Next step and no button', async () => {
    api.getState.mockResolvedValue(lifecycleOf({ currentStatusCode: 'DUPLICATED', isTerminal: true, canReopen: false }));
    renderPage();
    await waitFor(() => expect(status()).toHaveTextContent('This request was marked as a duplicate of another request. Nothing can be decided here; work on the other one.'));
    expect(screen.getAllByRole('status', { name: 'Next step' })).toHaveLength(1);
    expect(screen.queryByRole('group', { name: /Quote or skip/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Create RFQ|Save for a manager|Quote all|Open the lead/ })).toBeNull();
    expect(screen.queryByText(/reopen it from the lead page|duplicated/i)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[A-Z]{2,}_[A-Z]{2,}|DUPLICATED/);
  });

  // The server can send RFQ_REVISION_REQUIRED with no promotion receipt (the promoted revision or
  // decision row is missing). The record is locked, so there is no decision and no button, yet the
  // lines alone still ask for a choice. The sentence must not name a control that is not there.
  it('names no absent control when the record is locked but the screen has no finished state to report', async () => {
    record = {
      ...baseWorkbench(),
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      promotion: null,
      blockers: [{ code: 'RFQ_REVISION_REQUIRED', message: 'A reply or amendment created a newer immutable Lead revision after RFQ promotion.' }],
    };
    api.getState.mockReturnValue(new Promise(() => undefined));
    renderPage();
    await waitFor(() => expect(status()).toHaveTextContent('Nothing can be decided here right now. Open the lead to see where it stands.'));
    expect(status()).not.toHaveTextContent(/Create RFQ|Choose Quote or Skip|Ready:|Save for/);
    expect(screen.getAllByRole('status', { name: 'Next step' })).toHaveLength(1);
    expect(screen.queryByRole('group', { name: /Quote or skip/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Create RFQ|Save for a manager|Save for review|Quote all|Check the document|Choose the unit|Assign an owner/ })).toBeNull();
    expect(screen.queryByText(/newer immutable Lead revision/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[A-Z]{2,}_[A-Z]{2,}/);
    // One "Open the lead", in the panel beside the sentence that names it.
    expect(screen.getAllByRole('button', { name: 'Open the lead' })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Open the lead' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/view/407');
  });

  it('offers Open the lead on a declined request only to someone who may reopen it', async () => {
    api.getState.mockResolvedValue(lifecycleOf({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, canReopen: true }));
    renderPage();
    await waitFor(() => expect(status()).toHaveTextContent('This request was declined. To work on it again, reopen it on the lead page.'));
    fireEvent.click(screen.getByRole('button', { name: 'Open the lead' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/view/407');
  });

  it('tells a rep on a declined request to ask a manager', async () => {
    auth.user = { id: 9, isManager: false, isSuperAdmin: false, businessUnitId: 1 };
    api.getState.mockResolvedValue(lifecycleOf({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, canReopen: true }));
    renderPage();
    await waitFor(() => expect(status()).toHaveTextContent('This request was declined. Ask a manager to reopen it if the customer still wants a price.'));
    expect(screen.queryByRole('button', { name: 'Open the lead' })).toBeNull();
  });

  it('asks a manager to review a customer change, with Open the RFQ once, on the receipt', async () => {
    record = {
      ...baseWorkbench(),
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      promotion: { ...PROMOTION },
      blockers: [{ code: 'RFQ_REVISION_REQUIRED', message: 'A reply or amendment created a newer immutable Lead revision after RFQ promotion.', actionLabel: 'Open existing RFQ', actionPath: '/procurement/rfqs/view/417' }],
    };
    renderPage();
    expect(await screen.findByText('The customer changed this request after the RFQ was created')).toBeInTheDocument();
    expect(status()).toHaveTextContent('The customer changed this request after it became RFQ RFQ-2026-0417. Compare revision 2 with the RFQ, then press Review the change to record what you did.');
    expect(screen.getByText('Revision 2 arrived after RFQ RFQ-2026-0417 was created from revision 1. Reviewing it records what you did; the RFQ itself is not changed.')).toBeInTheDocument();
    expect(screen.queryByText(/newer immutable Lead revision/)).toBeNull();
    // The newer revision's line total is never put beside the RFQ's count.
    expect(screen.getByText(/ 3 lines went into the RFQ\./)).toBeInTheDocument();
    expect(screen.queryByText(/of \d+ lines went into the RFQ/)).toBeNull();
    await waitFor(() => expect(screen.getAllByText('Newer revision')).toHaveLength(3));

    fireEvent.click(screen.getByRole('button', { name: 'Open the RFQ' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/rfqs/view/417');
    fireEvent.click(screen.getByRole('button', { name: 'Review the change' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('Complete RFQ amendment review');
  });

  it('tells anyone who cannot review a customer change to ask a manager, and offers the RFQ', async () => {
    auth.user = { id: 9, isManager: false, isSuperAdmin: false, businessUnitId: 1 };
    record = {
      ...baseWorkbench(),
      leadRevisionId: 9002,
      leadRevisionNumber: 2,
      promotion: { ...PROMOTION },
      blockers: [{ code: 'RFQ_REVISION_REQUIRED', message: 'server words' }],
    };
    renderPage();
    expect(await screen.findByText('The customer changed this request after the RFQ was created')).toBeInTheDocument();
    expect(status()).toHaveTextContent('The customer changed this request after it became RFQ RFQ-2026-0417. Ask a manager to review the change.');
    expect(screen.queryByRole('button', { name: 'Review the change' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open the RFQ' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/rfqs/view/417');
  });

  it('says an RFQ from before this screen carries on from the RFQ, with the one control in the panel', async () => {
    record = { ...baseWorkbench(), blockers: [{ code: 'LEGACY_RFQ', message: 'A formal RFQ already exists for this Lead without a governed promotion receipt.', actionLabel: 'Open existing RFQ', actionPath: '/procurement/rfqs/view/88' }] };
    renderPage();
    expect(await screen.findByText('Became an RFQ before this screen recorded decisions')).toBeInTheDocument();
    expect(status()).toHaveTextContent('This request already has an RFQ, created before decisions were recorded on this screen. The work carries on from the RFQ.');
    expect(screen.queryByText(/governed promotion receipt/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open existing RFQ' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open the RFQ' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/rfqs/view/88');
  });

  it('says a converted request with no RFQ needs an administrator, in plain words', async () => {
    record = { ...baseWorkbench(), blockers: [{ code: 'INCONSISTENT_CONVERTED_STATE', message: 'This Lead is marked converted but no formal RFQ exists.' }] };
    renderPage();
    expect(await screen.findByText('This record needs an administrator')).toBeInTheDocument();
    expect(status()).toHaveTextContent('This request is marked as an RFQ, but no RFQ exists. Ask an administrator to repair it; nothing can be decided here.');
    expect(screen.getByText('Nexora shows this request as having become an RFQ, but there is no RFQ behind it. Nothing here can be changed until it is repaired.')).toBeInTheDocument();
    expect(screen.queryByText(/marked converted/)).toBeNull();
  });
});

describe('a view-only role and a saved draft', () => {
  it('offers a view-only role no button it can never use, and names who to ask', async () => {
    auth.hasPermission = (_moduleName, action = 'view') => action === 'view';
    renderPage();
    await waitFor(() => expect(status()).toHaveTextContent('Your role can view this request but not decide it. Ask Golden Salesperson or a manager to change it.'));
    expect(screen.queryByRole('button', { name: /Create RFQ|Save for a manager|Save for review|Assign an owner/ })).toBeNull();
    expect(screen.getByText("Your role can't change the owner.")).toBeInTheDocument();
  });

  it('shows a saved draft to a view-only role as the saved draft', async () => {
    auth.hasPermission = (_moduleName, action = 'view') => action === 'view';
    record = { ...baseWorkbench(), participationStatus: 'DRAFT', participationVersion: 1, lines: savedLines({ decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }) };
    renderPage();
    await waitFor(() => expect(screen.getByText(/lines marked to quote in the saved draft/).textContent).toBe('3 of 3 lines marked to quote in the saved draft'));
    expect(screen.getByText('Saved as a draft.')).toBeInTheDocument();
  });

  it('tells a manager with a ready draft to check it and press Create RFQ', async () => {
    record = { ...baseWorkbench(), participationStatus: 'DRAFT', participationVersion: 1, lines: savedLines({ decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }) };
    renderPage();
    expect(await screen.findByText('Saved as a draft. Check the choices, then press Create RFQ.')).toBeInTheDocument();
    expect(screen.queryByText(/for Golden Salesperson/)).toBeNull();
  });

  it('tells a rep with a ready draft that a manager creates the RFQ', async () => {
    auth.user = { id: 9, isManager: false, isSuperAdmin: false, businessUnitId: 1 };
    record = { ...baseWorkbench(), participationStatus: 'DRAFT', participationVersion: 1, lines: savedLines({ decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }) };
    renderPage();
    expect(await screen.findByText('Saved as a draft. A manager creates the RFQ from here.')).toBeInTheDocument();
  });
});

describe('the facts and the history in job words', () => {
  it('says "Not recorded" for a missing received time, and names the uploader when the server says who', async () => {
    record = { ...baseWorkbench(), receivedAtUtc: null };
    const view = renderPage();
    // The value sits beside its label; "Not recorded" also appears in the history fold.
    expect(await screen.findByText('Received')).toBeInTheDocument();
    expect(screen.getByText('Received').nextElementSibling).toHaveTextContent(/^Not recorded$/);
    view.unmount();

    record = { ...baseWorkbench(), receivedAtUtc: null, ...{ sourceChannel: 'Upload', uploadedAtUtc: '2026-09-12T08:00:00Z', uploadedBy: 'sara@nexora.test', uploadedByName: 'Sara Bin Ali' } };
    renderPage();
    expect(await screen.findByText('Uploaded')).toBeInTheDocument();
    expect(screen.getByText('Uploaded').nextElementSibling).toHaveTextContent(/^.+ by Sara Bin Ali$/);
    expect(screen.queryByText('Received')).toBeNull();
  });

  it('prints status, concern answer and line choices as words, not codes', async () => {
    record = {
      ...promotedRecord(),
      lifecycleStatusCode: 'CONVERTED_TO_RFQ',
      lifecycleStatusLabel: 'Converted to RFQ',
      fitAssessment: { ...baseWorkbench().fitAssessment!, version: 2, overallDecision: 'FIT', assessedBy: 'zack@kodekinetics.com', assessedAtUtc: '2026-09-09T08:59:00Z' },
    };
    renderPage();
    expect(await screen.findByText('Became an RFQ')).toBeInTheDocument();
    expect(screen.getByText(/^No concerns · zack@kodekinetics\.com · /)).toBeInTheDocument();
    expect(screen.getByText('Decided (version 1)')).toBeInTheDocument();
    expect(screen.getByText('Concern answer')).toBeInTheDocument();
    expect(screen.getByText('Line choices')).toBeInTheDocument();
    expect(screen.queryByText(/committed|converted_to_rfq|converted to rfq|v\d ·/i)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[A-Z]{2,}_[A-Z]{2,}|COMMITTED/);
  });
});
