import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LeadRevisionDTO } from '../../api/services/leadService';

/**
 * A revision reads as what changed. The forty "Unchanged" rows a rep used to scroll past are
 * gone, and a timestamp re-serialised to a different precision is not a change.
 */

const getRevisions = vi.fn();
vi.mock('../../api/services/leadService', () => ({
  default: { getRevisions: (...args: unknown[]) => getRevisions(...args) },
}));

import LeadRevisionTimeline, { fieldLabel, isRealChange, objectChanges } from './LeadRevisionTimeline';

const revision = (overrides: Partial<LeadRevisionDTO>): LeadRevisionDTO => ({
  id: 1,
  revisionNumber: 1,
  createdAtUtc: '2026-09-10T00:51:00Z',
  fingerprint: '878bd97aa0b94d9a99d0a52c80ce13ec267b18df5d020cb0f9123ef5b6e21621',
  customerRfqReference: 'VISIBLE-FLOW-900',
  processingPath: 'HumanReview',
  externalAiUsed: false,
  differences: [],
  impacts: [],
  ...overrides,
});

const renderTimeline = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><LeadRevisionTimeline leadId={5} /></MemoryRouter>
    </QueryClientProvider>,
  );
};

beforeEach(() => vi.clearAllMocks());

describe('LeadRevisionTimeline', () => {
  it('shows only what changed, in words, and never an Unchanged row', async () => {
    getRevisions.mockResolvedValue([
      revision({
        id: 2,
        revisionNumber: 2,
        differences: [
          { changeType: 'Unchanged', scope: 'Header', path: '$.requiredDeliveryDate', previousValueJson: 'null', currentValueJson: 'null' },
          { changeType: 'Unchanged', scope: 'Header', path: '$.deliveryLocation', previousValueJson: '"Dammam"', currentValueJson: '"Dammam"' },
          { changeType: 'Modified', scope: 'Line', path: '$.items[1].quantity', previousValueJson: '4', currentValueJson: '6' },
          { changeType: 'Modified', scope: 'Header', path: '$.receivedAtUtc', previousValueJson: '"2026-09-10T00:51:00Z"', currentValueJson: '"2026-09-10T00:51:00.0000000Z"' },
          { changeType: 'Added', scope: 'Header', path: '$.bidClosingDate', previousValueJson: null, currentValueJson: '"2026-10-15"' },
        ],
      }),
      revision({ id: 1, revisionNumber: 1, processingPath: 'ManualUpload' }),
    ]);
    renderTimeline();

    const current = await screen.findByRole('button', { name: 'Revision 2, 2 changes' });
    fireEvent.click(current);
    expect(screen.getByText('Line 2 · Quantity')).toBeInTheDocument();
    expect(screen.getByText('Quote due')).toBeInTheDocument();
    expect(screen.getByText('Added: 2026-10-15')).toBeInTheDocument();
    expect(screen.queryByText(/Unchanged/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Received at utc/)).not.toBeInTheDocument();
    expect(screen.queryByText(/requiredDeliveryDate/)).not.toBeInTheDocument();
    expect(screen.getByText('Checked by a person')).toBeInTheDocument();

    const first = screen.getByRole('button', { name: 'Revision 1, First version' });
    fireEvent.click(first);
    expect(screen.getByText('What the customer asked for, as first received.')).toBeInTheDocument();
  });

  it('says plainly when a revision changed nothing the customer asked for', async () => {
    getRevisions.mockResolvedValue([
      revision({ id: 3, revisionNumber: 3, differences: [
        { changeType: 'Unchanged', scope: 'Header', path: '$.buyer', previousValueJson: '"Ali"', currentValueJson: '"Ali"' },
      ] }),
    ]);
    renderTimeline();
    const row = await screen.findByRole('button', { name: 'Revision 3, No changes to the request' });
    fireEvent.click(row);
    expect(screen.getByText('Nothing the customer asked for changed. This revision records that it was checked by a person.')).toBeInTheDocument();
    // The fingerprint is still there for an auditor, shortened, with the full value on hover.
    const fingerprint = screen.getByTitle('878bd97aa0b94d9a99d0a52c80ce13ec267b18df5d020cb0f9123ef5b6e21621');
    expect(within(fingerprint.parentElement!).getByText(/fingerprint 878bd97aa0b9…/)).toBeInTheDocument();
  });

  it('never renders an outage as an empty history', async () => {
    getRevisions.mockRejectedValue(new Error('boom'));
    renderTimeline();
    expect(await screen.findByText(/Revision history is temporarily unavailable/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Retry/ })).toBeInTheDocument();
    expect(screen.queryByText(/No revisions have been recorded/)).not.toBeInTheDocument();
  });
});

describe('what counts as a change', () => {
  it('treats the same instant written at two precisions, or with and without a Z, as unchanged', () => {
    expect(isRealChange({ changeType: 'Modified', scope: 'Header', path: '$.receivedAtUtc', previousValueJson: '"2026-09-10T00:51:00Z"', currentValueJson: '"2026-09-10T00:51:00.0000000Z"' })).toBe(false);
    expect(isRealChange({ changeType: 'Modified', scope: 'Field', path: '$.recDate', previousValueJson: '"2026-09-10T04:11:28.236147Z"', currentValueJson: '"2026-09-10T04:11:28.236147"' })).toBe(false);
  });

  it('ignores the review marker the server strips from remarks on approval', () => {
    expect(isRealChange({ changeType: 'Modified', scope: 'Field', path: '$.headerRemarks', previousValueJson: '"[NEEDS REVIEW] Received date is missing"', currentValueJson: '"Received date is missing"' })).toBe(false);
    expect(isRealChange({ changeType: 'Modified', scope: 'Field', path: '$.headerRemarks', previousValueJson: '"Deliver to Dammam"', currentValueJson: '"Deliver to Jubail"' })).toBe(true);
    expect(isRealChange({ changeType: 'Modified', scope: 'Header', path: '$.bidClosingDate', previousValueJson: '"2026-09-10"', currentValueJson: '"2026-09-18"' })).toBe(true);
    expect(isRealChange({ changeType: 'Modified', scope: 'Line', path: '$.items[0].quantity', previousValueJson: '4', currentValueJson: '4' })).toBe(false);
    expect(isRealChange({ changeType: 'Removed', scope: 'Line', path: '$.items[3]', previousValueJson: '{"quantity":1}', currentValueJson: null })).toBe(true);
  });

  it('reads a whole changed line as the fields inside it that differ', () => {
    expect(objectChanges(
      { line: '1', quantity: 3, uom: null, unitOfMeasure: null, currency: null, aiConfidence: 1, receivedDate: '2026-09-10T00:00:00Z' },
      { line: '1', quantity: 3, uom: 'au', unitOfMeasure: 'AU', currency: 'SAR', aiConfidence: 1.0, receivedDate: '2026-09-10T00:00:00' },
    )).toEqual([
      { key: 'unitOfMeasure', before: 'Not stated', after: 'AU' },
      { key: 'currency', before: 'Not stated', after: 'SAR' },
    ]);
    expect(objectChanges('a', 'b')).toEqual([]);
  });

  it('names fields the way a person would', () => {
    expect(fieldLabel('$.items["00020"]')).toBe('Line 00020');
    expect(fieldLabel('$.requiredDeliveryDate')).toBe('Required delivery');
    expect(fieldLabel('$.items[2].manufacturerPartNumber')).toBe('Line 3 · Part number');
    expect(fieldLabel('$.recDate')).toBe('Received');
    expect(fieldLabel('$.buyer')).toBe('Buyer');
    expect(fieldLabel('$.opportunityNo')).toBe('Opportunity no');
  });
});
