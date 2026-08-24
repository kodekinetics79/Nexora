import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LeadOwnerHistory from './LeadOwnerHistory';

/**
 * `GET /api/commercial-intelligence/leads/{id}/assignment-history` has recorded every owner
 * change — previous owner, new owner, reason, timestamp — since governed routing shipped, and had
 * ZERO frontend callers. The trail existed for nobody to read.
 *
 * What it returns is machine vocabulary (`MANUAL_ASSIGNMENT`, `CustomerPermanent`), so the test
 * that matters is that a person never sees any of it.
 */

const getLeadAssignmentHistory = vi.fn();

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: { getLeadAssignmentHistory: (leadId: number) => getLeadAssignmentHistory(leadId) },
  };
});

const renderPanel = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <LeadOwnerHistory leadId={101} />
    </QueryClientProvider>,
  );
};

const open = () => fireEvent.click(screen.getByRole('button', { name: /owner history/i }));

describe('LeadOwnerHistory', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('saysWhoHadItAndWhy_inWordsRatherThanCodes', async () => {
    getLeadAssignmentHistory.mockResolvedValue([
      {
        id: 2,
        leadId: 101,
        previousOwnerUserId: 77,
        ownerUserId: 2,
        previousOwnerName: 'Tariq Al-Harbi',
        ownerName: 'Sara Bin Ali',
        scope: 'CustomerPermanent',
        reasonCode: 'MANUAL_ASSIGNMENT',
        comment: null,
        effectiveFrom: '2026-08-20T09:00:00Z',
        effectiveTo: null,
      },
      {
        id: 1,
        leadId: 101,
        previousOwnerUserId: null,
        ownerUserId: 77,
        previousOwnerName: null,
        ownerName: 'Tariq Al-Harbi',
        scope: 'LeadOnly',
        reasonCode: 'PRIMARY_OWNER_ASSIGNED',
        comment: null,
        effectiveFrom: '2026-08-18T09:00:00Z',
        effectiveTo: '2026-08-20T09:00:00Z',
      },
    ]);
    renderPanel();
    open();

    expect(await screen.findByText(/moved from tariq al-harbi to sara bin ali/i)).toBeInTheDocument();
    expect(screen.getByText(/assigned to tariq al-harbi/i)).toBeInTheDocument();
    // The stored code is the audit value; what a person reads is the sentence it stands for.
    expect(screen.getByText(/assigned by hand/i)).toBeInTheDocument();
    expect(screen.getByText(/assigned automatically to the account’s primary owner/i)).toBeInTheDocument();
    expect(screen.getByText(/also made the permanent owner of this customer/i)).toBeInTheDocument();

    // Not one raw enum or decision code anywhere on screen.
    expect(document.body.textContent).not.toMatch(/MANUAL_ASSIGNMENT|PRIMARY_OWNER_ASSIGNED|CustomerPermanent|LeadOnly/);
  });

  it('prefersTheReasonAPersonTyped_overTheEngineSentence', async () => {
    getLeadAssignmentHistory.mockResolvedValue([{
      id: 3,
      leadId: 101,
      previousOwnerUserId: 77,
      ownerUserId: 2,
      previousOwnerName: 'Tariq Al-Harbi',
      ownerName: 'Sara Bin Ali',
      scope: 'LeadOnly',
      reasonCode: 'MANUAL_ASSIGNMENT',
      comment: 'Tariq is on leave until the 3rd',
      effectiveFrom: '2026-08-20T09:00:00Z',
      effectiveTo: null,
    }]);
    renderPanel();
    open();

    expect(await screen.findByText(/tariq is on leave until the 3rd/i)).toBeInTheDocument();
    expect(screen.queryByText(/assigned by hand/i)).not.toBeInTheDocument();
  });

  it('saysItHasNeverChangedHands_whenThereIsNothingRecorded', async () => {
    getLeadAssignmentHistory.mockResolvedValue([]);
    renderPanel();
    open();

    expect(await screen.findByText(/never changed hands/i)).toBeInTheDocument();
  });

  it('neverReportsAFailedReadAsAnEmptyHistory', async () => {
    getLeadAssignmentHistory.mockRejectedValue(new Error('network'));
    renderPanel();
    open();

    expect(await screen.findByText(/couldn't load the owner history/i)).toBeInTheDocument();
    expect(screen.queryByText(/never changed hands/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });

  it('doesNotFetchTheTrailUntilSomebodyOpensIt', async () => {
    getLeadAssignmentHistory.mockResolvedValue([]);
    renderPanel();

    expect(getLeadAssignmentHistory).not.toHaveBeenCalled();
    open();
    expect(await screen.findByText(/never changed hands/i)).toBeInTheDocument();
    expect(getLeadAssignmentHistory).toHaveBeenCalledWith(101);
  });
});
