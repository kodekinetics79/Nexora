import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';

/**
 * The document beside the lines, on the same screen. What the rep confirms goes through the one
 * governed review call, carrying every current line so nothing is deleted by omission.
 */

const snack = vi.fn();
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: snack }) }));

const api = { getById: vi.fn(), submitReview: vi.fn(), fetchObjectUrl: vi.fn() };
vi.mock('../../../api/services/leadService', () => ({
  default: { getById: (...args: unknown[]) => api.getById(...args) },
}));
vi.mock('../../../api/services/extractionReviewService', () => ({
  default: { submitReview: (...args: unknown[]) => api.submitReview(...args) },
}));
vi.mock('../../../utils/authenticatedFile', () => ({
  fetchAuthenticatedObjectUrl: (...args: unknown[]) => api.fetchObjectUrl(...args),
  openAuthenticatedFile: vi.fn(),
  downloadAuthenticatedFile: vi.fn(),
}));

import CheckDocumentDialog, { DEFAULT_CHECK_REASON } from './CheckDocumentDialog';

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

const workbench = (overrides: Partial<LeadDecisionWorkbenchDTO> = {}): LeadDecisionWorkbenchDTO => ({
  leadId: 5,
  leadRevisionId: 50,
  leadRevisionNumber: 1,
  decisionVersion: 1,
  participationStatus: 'NONE',
  lifecycleStatusCode: 'RECEIVED',
  hasFrozenCommercialHeader: true,
  verificationStatus: 'NEEDS_REVIEW',
  evidence: [{ occurrenceId: 9, kind: 'DOCUMENT', name: 'SEC_RFQ.pdf', mediaType: 'application/pdf', status: 'Received', sourceAvailable: true, downloadUrl: '/api/evidence/9' }],
  lines: [
    line({ id: 1, description: 'Control module', verificationStatus: 'NEEDS_CHECK', unitOfMeasure: null, currency: null, verificationDetail: 'Unit of measure missing' }),
    line({ id: 2, description: 'Cable gland kit' }),
  ],
  reasonCodes: [],
  unitOptions: [{ code: 'EA', label: 'Each' }, { code: 'SET', label: 'Set' }],
  currencyOptions: [{ code: 'SAR', label: 'Saudi riyal' }],
  fitAssessment: null,
  promotion: null,
  blockers: [],
  ...overrides,
});

const lead = {
  id: 5,
  reviewVersion: 3,
  bidClosingDate: '2026-10-01T00:00:00Z',
  leadItems: [
    { id: 501, lineItemNo: '00001', productShortName: 'Control module', quantity: 3, aiconfidence: 0.5, manufacturerPartNumber: 'CM-900' },
    { id: 502, lineItemNo: '00002', productShortName: 'Cable gland kit', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR', aiconfidence: 0.9 },
  ],
};

const renderDialog = (props: Partial<React.ComponentProps<typeof CheckDocumentDialog>> = {}) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const onConfirmed = vi.fn();
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <CheckDocumentDialog open leadId={5} workbench={workbench()} onClose={onClose} onConfirmed={onConfirmed} {...props} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { onConfirmed, onClose };
};

const pickOption = async (comboboxName: string, optionName: string | RegExp) => {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: comboboxName }));
  fireEvent.click(within(await screen.findByRole('listbox')).getByRole('option', { name: optionName }));
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getById.mockResolvedValue(lead);
  api.fetchObjectUrl.mockResolvedValue({ url: 'blob:doc', contentType: 'application/pdf', blob: new Blob(['%PDF']) });
  api.submitReview.mockResolvedValue({ ...lead, reviewVersion: 4 });
});

describe('CheckDocumentDialog', () => {
  it('shows the document and only the lines that need checking, prefilled from the record', async () => {
    renderDialog();
    expect(await screen.findByTitle('SEC_RFQ.pdf')).toHaveAttribute('src', 'blob:doc');
    expect(api.fetchObjectUrl).toHaveBeenCalledWith('/api/evidence/9');
    expect(await screen.findByText('1 of 2 lines to check')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'What they asked for, line 00001' })).toHaveValue('Control module');
    expect(screen.getByRole('textbox', { name: 'Maker part number, line 00001' })).toHaveValue('CM-900');
    expect(screen.getByRole('spinbutton', { name: 'Quantity, line 00001' })).toHaveValue(3);
    expect(screen.queryByRole('textbox', { name: 'What they asked for, line 00002' })).not.toBeInTheDocument();
    // Nothing can be confirmed while the unit is still missing.
    expect(screen.getByRole('button', { name: 'Confirm what the document says' })).toBeDisabled();
  });

  it('confirms through the governed review call with every line, the corrections and a reason', async () => {
    const { onConfirmed, onClose } = renderDialog();
    await screen.findByText('1 of 2 lines to check');
    await pickOption('Unit, line 00001', 'SET');
    await pickOption('Currency, line 00001', 'SAR');
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Quantity, line 00001' }), { target: { value: '6' } });
    const confirm = screen.getByRole('button', { name: 'Confirm what the document says' });
    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);

    await waitFor(() => expect(api.submitReview).toHaveBeenCalled());
    const [id, payload] = api.submitReview.mock.calls[0];
    expect(id).toBe(5);
    expect(payload.action).toBe('approve');
    expect(payload.expectedVersion).toBe(3);
    expect(payload.reason).toBe(DEFAULT_CHECK_REASON);
    expect(payload.header).toEqual({});
    expect(payload.items).toHaveLength(2);
    expect(payload.items[0]).toMatchObject({ id: 501, productShortName: 'Control module', quantity: 6, unitOfMeasure: 'SET', currency: 'SAR', manufacturerPartNumber: 'CM-900' });
    expect(payload.items[1]).toMatchObject({ id: 502, quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' });

    await waitFor(() => expect(onConfirmed).toHaveBeenCalled());
    expect(onConfirmed.mock.calls[0][0]).toContainEqual({ lineItemNo: '00001', quantity: 6, unitOfMeasure: 'SET', currency: 'SAR' });
    expect(onClose).toHaveBeenCalled();
    expect(snack).toHaveBeenCalledWith('Confirmed against the document.', { variant: 'success' });
  });

  it('shows a spreadsheet or text document as text, and lists one file once', async () => {
    api.fetchObjectUrl.mockResolvedValue({ url: 'blob:csv', contentType: 'text/csv', blob: new Blob(['RFQ,Item\nSEC-1,Control module']) });
    const evidence = workbench().evidence[0];
    renderDialog({ workbench: workbench({ evidence: [
      { ...evidence, name: 'inquiry.csv', mediaType: 'text/csv' },
      { ...evidence, name: 'inquiry.csv', mediaType: 'text/csv', occurrenceId: 10 },
    ] }) });
    expect(await screen.findByLabelText('inquiry.csv')).toHaveTextContent('SEC-1,Control module');
    expect(screen.queryByTitle('inquiry.csv')).not.toBeInTheDocument();
    expect(screen.getAllByText('inquiry.csv')).toHaveLength(1);
  });

  it('offers to open or download a spreadsheet the browser cannot draw, without downloading it first', async () => {
    const evidence = { ...workbench().evidence[0], name: 'bid-list.xlsx', mediaType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' };
    renderDialog({ workbench: workbench({ evidence: [evidence] }) });
    expect(await screen.findByRole('button', { name: 'Open in a new tab' })).toBeInTheDocument();
    expect(screen.getByText(/is a file the browser cannot show here/)).toBeInTheDocument();
    expect(screen.queryByLabelText('bid-list.xlsx')).not.toBeInTheDocument();
    expect(api.fetchObjectUrl).not.toHaveBeenCalled();
  });

  it('frames an HTML document rather than printing its tags', async () => {
    api.fetchObjectUrl.mockResolvedValue({ url: 'blob:html', contentType: 'text/html', blob: new Blob(['<table><tr><td>x</td></tr></table>']) });
    const evidence = { ...workbench().evidence[0], name: 'inquiry.html', mediaType: 'text/html' };
    renderDialog({ workbench: workbench({ evidence: [evidence] }) });
    expect(await screen.findByTitle('inquiry.html')).toHaveAttribute('src', 'blob:html');
    expect(screen.queryByText(/<table>/)).not.toBeInTheDocument();
  });

  it('keeps a refused check on the form with the way out, and reveals the lines it had hidden', async () => {
    api.submitReview.mockRejectedValue(new Error('This lead is no longer awaiting extraction review.'));
    renderDialog();
    await screen.findByText('1 of 2 lines to check');
    await pickOption('Unit, line 00001', 'EA');
    fireEvent.click(screen.getByRole('button', { name: 'Confirm what the document says' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/no longer awaiting extraction review|could not be recorded/);
    expect(screen.getByRole('button', { name: 'Open the full review' })).toBeInTheDocument();
    // Every line is now visible, in case the fault is on one the dialog had hidden.
    expect(screen.getByRole('textbox', { name: 'What they asked for, line 00002' })).toBeInTheDocument();
  });

  it('sends a changed quote-due date with the check, and nothing else from the header', async () => {
    renderDialog();
    await screen.findByText('1 of 2 lines to check');
    await pickOption('Unit, line 00001', 'EA');
    fireEvent.change(screen.getByLabelText('Quote due'), { target: { value: '2026-10-15' } });
    fireEvent.click(screen.getByRole('button', { name: 'Confirm what the document says' }));
    await waitFor(() => expect(api.submitReview).toHaveBeenCalled());
    expect(api.submitReview.mock.calls[0][1].header).toEqual({ bidClosingDate: '2026-10-15' });
  });

  it('says so when no document is on file', async () => {
    renderDialog({ workbench: workbench({ evidence: [] }) });
    expect(await screen.findByText(/No document is on file for this request/)).toBeInTheDocument();
    expect(await screen.findByText('1 of 2 lines to check')).toBeInTheDocument();
  });
});
